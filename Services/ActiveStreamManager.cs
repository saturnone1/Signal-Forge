using System.Collections.Concurrent;
using System.Reflection;
using Google.Protobuf;
using Grpc.Core;
using GrpcWorkbench.Grpc;
using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Models.Session;

namespace GrpcWorkbench.Services;

/// <summary>
/// 활성 gRPC 스트리밍 세션을 관리합니다.
/// 클라이언트가 메시지를 1건씩 보낼 수 있도록 스트림을 열어둡니다.
/// </summary>
public interface IActiveStreamManager
{
    /// <summary>Client/Bidirectional 스트림을 열고 streamId를 반환</summary>
    Task<string> OpenStreamAsync(GrpcRequestPayload payload, GrpcSession session);

    /// <summary>열린 스트림에 메시지 1건 Write</summary>
    Task WriteMessageAsync(string streamId, string messageJson);

    /// <summary>스트림을 닫고 응답을 반환</summary>
    Task<StreamCloseResult> CloseStreamAsync(string streamId);

    /// <summary>스트림이 열려있는지 확인</summary>
    bool IsStreamOpen(string streamId);
}

public class StreamCloseResult
{
    public bool IsSuccess { get; set; }
    public string? Response { get; set; }
    public List<string>? Messages { get; set; }
    public string? Error { get; set; }
}

public class ActiveStreamContext
{
    public required string StreamId { get; init; }
    public required string RpcType { get; init; }
    public required object StreamCall { get; init; }
    public required object RequestStream { get; init; }
    public required MethodInfo WriteAsyncMethod { get; init; }
    public required MethodInfo CompleteAsyncMethod { get; init; }
    public required Type RequestMessageType { get; init; }
    public required IJsonMessageConverter JsonConverter { get; init; }

    // Bidirectional 전용
    public object? ResponseStream { get; init; }
    public MethodInfo? MoveNextMethod { get; init; }
    public PropertyInfo? CurrentProperty { get; init; }

    // Client streaming 전용
    public PropertyInfo? ResponseAsyncProp { get; init; }

    public int MessagesSent { get; set; }
    public List<string> ReceivedMessages { get; } = [];
    public Task? ReceiveTask { get; set; }
}

public class ActiveStreamManager : IActiveStreamManager
{
    private readonly ConcurrentDictionary<string, ActiveStreamContext> _streams = new();
    private readonly IGrpcChannelProvider _channelProvider;
    private readonly IJsonMessageConverter _jsonConverter;
    private readonly IGrpcServiceClientFinder _clientFinder;
    private readonly IDynamicProtoCompiler _protoCompiler;
    private readonly ILogger<ActiveStreamManager> _logger;

    public ActiveStreamManager(
        IGrpcChannelProvider channelProvider,
        IJsonMessageConverter jsonConverter,
        IGrpcServiceClientFinder clientFinder,
        IDynamicProtoCompiler protoCompiler,
        ILogger<ActiveStreamManager> logger)
    {
        _channelProvider = channelProvider;
        _jsonConverter = jsonConverter;
        _clientFinder = clientFinder;
        _protoCompiler = protoCompiler;
        _logger = logger;
    }

    public async Task<string> OpenStreamAsync(GrpcRequestPayload payload, GrpcSession session)
    {
        var assembly = await _protoCompiler.CompileProtoToAssemblyAsync(session.ProtoContent ?? []);
        var channel = await _channelProvider.GetChannelAsync(session);

        var metadata = new Metadata();
        if (payload.Metadata != null)
        {
            foreach (var kvp in payload.Metadata)
                metadata.Add(kvp.Key, kvp.Value);
        }

        var callOptions = new CallOptions(metadata, DateTime.UtcNow.AddSeconds(payload.TimeoutSeconds));

        var clientType = _clientFinder.FindServiceClientType(assembly, payload.ServiceName)
            ?? throw new InvalidOperationException($"Service client for '{payload.ServiceName}' not found");

        var clientInstance = Activator.CreateInstance(clientType, channel)
            ?? throw new InvalidOperationException($"Failed to create client instance");

        var methodInfo = clientType.GetMethods()
            .Where(m => m.Name == payload.MethodName)
            .FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 1 && p[0].ParameterType == typeof(CallOptions);
            })
            ?? clientType.GetMethods().FirstOrDefault(m => m.Name == payload.MethodName)
            ?? throw new InvalidOperationException($"Method '{payload.MethodName}' not found");

        var returnType = methodInfo.ReturnType;
        Type? requestMessageType = null;
        if (returnType.IsGenericType)
        {
            var genericArgs = returnType.GetGenericArguments();
            if (genericArgs.Length >= 1)
                requestMessageType = genericArgs[0];
        }

        if (requestMessageType == null)
            throw new InvalidOperationException(
                $"Could not determine request message type for method {payload.MethodName}");

        var streamCallResult = methodInfo.Invoke(clientInstance, [callOptions])!;

        var requestStreamProp = streamCallResult.GetType().GetProperty("RequestStream")
            ?? throw new InvalidOperationException("Could not find RequestStream");
        var requestStream = requestStreamProp.GetValue(streamCallResult)!;
        var writeAsyncMethod = requestStream.GetType().GetMethod("WriteAsync", [requestMessageType])
            ?? throw new InvalidOperationException("Could not find WriteAsync");
        var completeAsyncMethod = requestStream.GetType().GetMethod("CompleteAsync")
            ?? throw new InvalidOperationException("Could not find CompleteAsync");

        var streamId = Guid.NewGuid().ToString("N")[..12];

        // rpcType 판별: ResponseStream이 있으면 Bidirectional, ResponseAsync면 ClientStreaming
        var responseStreamProp = streamCallResult.GetType().GetProperty("ResponseStream");
        var responseAsyncProp = streamCallResult.GetType().GetProperty("ResponseAsync");

        var ctx = new ActiveStreamContext
        {
            StreamId = streamId,
            RpcType = responseStreamProp != null ? "BidirectionalStreaming" : "ClientStreaming",
            StreamCall = streamCallResult,
            RequestStream = requestStream,
            WriteAsyncMethod = writeAsyncMethod,
            CompleteAsyncMethod = completeAsyncMethod,
            RequestMessageType = requestMessageType,
            JsonConverter = _jsonConverter,
            ResponseAsyncProp = responseAsyncProp,
        };

        // Bidirectional: 응답 수신 태스크 시작
        if (responseStreamProp != null)
        {
            var responseStream = responseStreamProp.GetValue(streamCallResult)!;
            var moveNextMethod = responseStream.GetType().GetMethod("MoveNext", [typeof(CancellationToken)]);
            var currentProperty = responseStream.GetType().GetProperty("Current");

            ctx = new ActiveStreamContext
            {
                StreamId = streamId,
                RpcType = "BidirectionalStreaming",
                StreamCall = streamCallResult,
                RequestStream = requestStream,
                WriteAsyncMethod = writeAsyncMethod,
                CompleteAsyncMethod = completeAsyncMethod,
                RequestMessageType = requestMessageType,
                JsonConverter = _jsonConverter,
                ResponseStream = responseStream,
                MoveNextMethod = moveNextMethod,
                CurrentProperty = currentProperty,
            };

            ctx.ReceiveTask = Task.Run(async () =>
            {
                try
                {
                    while (moveNextMethod != null && currentProperty != null)
                    {
                        var moveNextTask = (Task<bool>)moveNextMethod.Invoke(responseStream, [CancellationToken.None])!;
                        var hasNext = await moveNextTask;
                        if (!hasNext) break;

                        var current = currentProperty.GetValue(responseStream);
                        var json = _jsonConverter.MessageToJson((IMessage)current!);
                        ctx.ReceivedMessages.Add(json);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error receiving bidirectional messages for stream {StreamId}", streamId);
                }
            });
        }

        _streams[streamId] = ctx;
        _logger.LogInformation("Stream opened: {StreamId} ({RpcType})", streamId, ctx.RpcType);

        return streamId;
    }

    public async Task WriteMessageAsync(string streamId, string messageJson)
    {
        if (!_streams.TryGetValue(streamId, out var ctx))
            throw new InvalidOperationException($"Stream '{streamId}' not found");

        var message = await ctx.JsonConverter.JsonToMessageAsync(messageJson, ctx.RequestMessageType);
        var writeTask = (Task)ctx.WriteAsyncMethod.Invoke(ctx.RequestStream, [message])!;
        await writeTask;
        ctx.MessagesSent++;

        _logger.LogDebug("Stream {StreamId}: message #{Count} sent", streamId, ctx.MessagesSent);
    }

    public async Task<StreamCloseResult> CloseStreamAsync(string streamId)
    {
        if (!_streams.TryRemove(streamId, out var ctx))
            throw new InvalidOperationException($"Stream '{streamId}' not found");

        try
        {
            // RequestStream 완료
            var completeTask = (Task)ctx.CompleteAsyncMethod.Invoke(ctx.RequestStream, [])!;
            await completeTask;

            if (ctx.RpcType == "ClientStreaming" && ctx.ResponseAsyncProp != null)
            {
                // Client streaming: 단일 응답 대기
                dynamic responseTask = ctx.ResponseAsyncProp.GetValue(ctx.StreamCall)!;
                var response = await responseTask;
                var responseJson = ctx.JsonConverter.MessageToJson((IMessage)response);

                return new StreamCloseResult { IsSuccess = true, Response = responseJson };
            }
            else if (ctx.RpcType == "BidirectionalStreaming" && ctx.ReceiveTask != null)
            {
                // Bidirectional: 수신 태스크 완료 대기
                await ctx.ReceiveTask;
                return new StreamCloseResult { IsSuccess = true, Messages = ctx.ReceivedMessages };
            }

            return new StreamCloseResult { IsSuccess = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing stream {StreamId}", streamId);
            return new StreamCloseResult { IsSuccess = false, Error = ex.Message };
        }
    }

    public bool IsStreamOpen(string streamId) => _streams.ContainsKey(streamId);
}
