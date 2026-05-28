using System.Collections.Concurrent;
using System.Reflection;
using Google.Protobuf;
using Grpc.Core;
using GrpcWorkbench.Grpc;
using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Models.Session;

namespace GrpcWorkbench.Services;

/// <summary>
/// 3가지 gRPC 스트리밍 RPC 타입을 모두 담당합니다.
/// - Server Streaming: 서버로부터 메시지를 수신하는 단방향 스트림
/// - Client Streaming: 클라이언트가 메시지를 1건씩 전송 후 단일 응답 수신
/// - Bidirectional Streaming: 클라이언트와 서버가 동시에 메시지를 교환
///
/// Client/Bidirectional 스트리밍은 SignalR Hub을 통해 메시지를 1건씩 실시간으로 전송할 수 있도록
/// OpenStream / WriteMessage / CloseStream 패턴을 사용합니다.
/// </summary>
public interface IGrpcStreamingService
{
    // Server Streaming
    Task ExecuteServerStreamingAsync(GrpcRequestPayload payload, GrpcSession session,
        Func<string, Task> onMessageReceived, Func<Exception?, Task> onCompleted);

    // Client / Bidirectional Streaming (open-write-close 패턴)
    Task<string> OpenStreamAsync(GrpcRequestPayload payload, GrpcSession session,
        Func<string, Task>? onBidiMessageReceived = null);
    Task WriteMessageAsync(string streamId, string messageJson);
    Task<StreamCloseResult> CloseStreamAsync(string streamId);
    bool IsStreamOpen(string streamId);
    IReadOnlyList<OpenLocalStreamInfo> SnapshotOpenStreams();
}

public class OpenLocalStreamInfo
{
    public required string StreamId { get; init; }
    public required string SessionId { get; init; }
    public required string ServiceName { get; init; }
    public required string MethodName { get; init; }
    public required string RpcType { get; init; }
    public required DateTime OpenedAt { get; init; }
    public int MessagesSent { get; init; }
    public int ReceivedCount { get; init; }
}

public class StreamCloseResult
{
    public bool IsSuccess { get; set; }
    public string? Response { get; set; }
    public List<string>? Messages { get; set; }
    public string? Error { get; set; }
}

internal class ActiveStreamContext
{
    public required string StreamId { get; init; }
    public required string SessionId { get; init; }
    public required string ServiceName { get; init; }
    public required string MethodName { get; init; }
    public required string RpcType { get; init; }
    public required DateTime OpenedAt { get; init; }
    public required object StreamCall { get; init; }
    public required object RequestStream { get; init; }
    public required MethodInfo WriteAsyncMethod { get; init; }
    public required MethodInfo CompleteAsyncMethod { get; init; }
    public required Type RequestMessageType { get; init; }
    public required IJsonMessageConverter JsonConverter { get; init; }
    public object? ResponseStream { get; init; }
    public MethodInfo? MoveNextMethod { get; init; }
    public PropertyInfo? CurrentProperty { get; init; }
    public PropertyInfo? ResponseAsyncProp { get; init; }
    public int MessagesSent { get; set; }
    // 백그라운드 수신 루프가 Enqueue, CloseStream이 읽으므로 스레드 안전 컬렉션 사용
    public ConcurrentQueue<string> ReceivedMessages { get; } = new();
    public Task? ReceiveTask { get; set; }
}

public class GrpcStreamingService : IGrpcStreamingService
{
    private readonly ConcurrentDictionary<string, ActiveStreamContext> _streams = new();
    private readonly IGrpcChannelProvider _channelProvider;
    private readonly IJsonMessageConverter _jsonConverter;
    private readonly IGrpcServiceClientFinder _clientFinder;
    private readonly IDynamicProtoCompiler _protoCompiler;
    private readonly ILogger<GrpcStreamingService> _logger;

    public GrpcStreamingService(
        IGrpcChannelProvider channelProvider,
        IJsonMessageConverter jsonConverter,
        IGrpcServiceClientFinder clientFinder,
        IDynamicProtoCompiler protoCompiler,
        ILogger<GrpcStreamingService> logger)
    {
        _channelProvider = channelProvider;
        _jsonConverter = jsonConverter;
        _clientFinder = clientFinder;
        _protoCompiler = protoCompiler;
        _logger = logger;
    }

    // ── Server Streaming ──────────────────────────────────────────────────────

    /// <summary>
    /// Server Streaming RPC를 실행합니다.
    /// 서버로부터 메시지를 수신할 때마다 <paramref name="onMessageReceived"/>를 호출하고,
    /// 스트림 종료 또는 에러 발생 시 <paramref name="onCompleted"/>를 호출합니다.
    /// </summary>
    public async Task ExecuteServerStreamingAsync(GrpcRequestPayload payload, GrpcSession session,
        Func<string, Task> onMessageReceived, Func<Exception?, Task> onCompleted)
    {
        try
        {
            var assembly = await _protoCompiler.CompileProtoToAssemblyAsync(session.ProtoContent ?? []);
            var channel = await _channelProvider.GetChannelAsync(session);
            var callOptions = BuildCallOptions(payload);

            var clientType = _clientFinder.FindServiceClientType(assembly, payload.ServiceName)
                ?? throw new InvalidOperationException($"Service client not found for '{payload.ServiceName}'");

            var clientInstance = Activator.CreateInstance(clientType, channel);
            var methodInfo = FindMethod(clientType, payload.MethodName, paramCount: 2)
                ?? throw new InvalidOperationException($"Method '{payload.MethodName}' not found");

            var requestMessageType = methodInfo.GetParameters().FirstOrDefault()?.ParameterType;
            if (requestMessageType == null || !typeof(IMessage).IsAssignableFrom(requestMessageType))
                throw new InvalidOperationException($"Could not determine request message type for '{payload.MethodName}'");

            var requestMessage = await _jsonConverter.JsonToMessageAsync(payload.RequestJson, requestMessageType);
            var streamCall = methodInfo.Invoke(clientInstance, [requestMessage, callOptions])!;

            var responseStreamProp = streamCall.GetType().GetProperty("ResponseStream")
                ?? throw new InvalidOperationException("ResponseStream not found on streaming call");

            var responseStream = responseStreamProp.GetValue(streamCall)!;
            var moveNextMethod = responseStream.GetType().GetMethod("MoveNext", [typeof(CancellationToken)])
                ?? throw new InvalidOperationException("MoveNext not found on ResponseStream");
            var currentProperty = responseStream.GetType().GetProperty("Current")
                ?? throw new InvalidOperationException("Current not found on ResponseStream");

            while (true)
            {
                var hasNext = await (Task<bool>)moveNextMethod.Invoke(responseStream, [CancellationToken.None])!;
                if (!hasNext) break;

                var current = (IMessage)currentProperty.GetValue(responseStream)!;
                await onMessageReceived(_jsonConverter.MessageToJson(current));
            }

            await onCompleted(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server streaming failed: {ServiceName}.{MethodName}",
                payload.ServiceName, payload.MethodName);
            await onCompleted(ex);
        }
    }

    // ── Client / Bidirectional Streaming (open-write-close) ──────────────────

    /// <summary>
    /// Client 또는 Bidirectional Streaming RPC 스트림을 엽니다.
    /// 반환된 streamId로 <see cref="WriteMessageAsync"/> 및 <see cref="CloseStreamAsync"/>를 호출하세요.
    /// </summary>
    public async Task<string> OpenStreamAsync(GrpcRequestPayload payload, GrpcSession session,
        Func<string, Task>? onBidiMessageReceived = null)
    {
        var assembly = await _protoCompiler.CompileProtoToAssemblyAsync(session.ProtoContent ?? []);
        var channel = await _channelProvider.GetChannelAsync(session);
        // 스트리밍은 사용자가 명시적으로 CloseStream을 호출할 때까지 유지되어야 하므로
        // deadline을 설정하지 않음 (deadline을 설정하면 스트림 열기 후 일정 시간 내에
        // CloseStream을 호출하지 않으면 DeadlineExceeded 오류 발생)
        var callOptions = BuildCallOptions(payload, noDeadline: true);

        var clientType = _clientFinder.FindServiceClientType(assembly, payload.ServiceName)
            ?? throw new InvalidOperationException($"Service client for '{payload.ServiceName}' not found");

        var clientInstance = Activator.CreateInstance(clientType, channel)
            ?? throw new InvalidOperationException("Failed to create client instance");

        // Client/Bidirectional 메서드는 CallOptions 1개만 받음
        var methodInfo = FindMethod(clientType, payload.MethodName, paramCount: 1)
            ?? throw new InvalidOperationException($"Method '{payload.MethodName}' not found");

        // 반환 타입의 제네릭 인자에서 요청 메시지 타입 추출
        var requestMessageType = methodInfo.ReturnType.IsGenericType
            ? methodInfo.ReturnType.GetGenericArguments().FirstOrDefault()
            : null;

        if (requestMessageType == null)
            throw new InvalidOperationException($"Could not determine request message type for '{payload.MethodName}'");

        var streamCall = methodInfo.Invoke(clientInstance, [callOptions])!;

        var requestStreamProp = streamCall.GetType().GetProperty("RequestStream")
            ?? throw new InvalidOperationException("RequestStream not found");
        var requestStream = requestStreamProp.GetValue(streamCall)!;
        var writeAsyncMethod = requestStream.GetType().GetMethod("WriteAsync", [requestMessageType])
            ?? throw new InvalidOperationException("WriteAsync not found on RequestStream");
        var completeAsyncMethod = requestStream.GetType().GetMethod("CompleteAsync")
            ?? throw new InvalidOperationException("CompleteAsync not found on RequestStream");

        var streamId = Guid.NewGuid().ToString("N")[..12];

        // ResponseStream이 있으면 Bidirectional, ResponseAsync만 있으면 ClientStreaming
        var responseStreamProp = streamCall.GetType().GetProperty("ResponseStream");
        var responseAsyncProp = streamCall.GetType().GetProperty("ResponseAsync");

        ActiveStreamContext ctx;

        if (responseStreamProp != null)
        {
            // Bidirectional: 응답 수신 루프를 백그라운드로 시작
            var responseStream = responseStreamProp.GetValue(streamCall)!;
            var moveNextMethod = responseStream.GetType().GetMethod("MoveNext", [typeof(CancellationToken)]);
            var currentProperty = responseStream.GetType().GetProperty("Current");

            ctx = new ActiveStreamContext
            {
                StreamId = streamId,
                SessionId = session.SessionId,
                ServiceName = payload.ServiceName,
                MethodName = payload.MethodName,
                RpcType = "BidirectionalStreaming",
                OpenedAt = DateTime.UtcNow,
                StreamCall = streamCall,
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
                        var hasNext = await (Task<bool>)moveNextMethod.Invoke(responseStream, [CancellationToken.None])!;
                        if (!hasNext) break;

                        var current = currentProperty.GetValue(responseStream);
                        var json = _jsonConverter.MessageToJson((IMessage)current!);
                        ctx.ReceivedMessages.Enqueue(json);
                        if (onBidiMessageReceived != null)
                            await onBidiMessageReceived(json);
                    }
                }
                catch (Exception ex)
                {
                    _streams.TryRemove(streamId, out _);
                    _logger.LogError(ex, "Error receiving bidirectional messages for stream {StreamId}", streamId);
                }
            });
        }
        else
        {
            ctx = new ActiveStreamContext
            {
                StreamId = streamId,
                SessionId = session.SessionId,
                ServiceName = payload.ServiceName,
                MethodName = payload.MethodName,
                RpcType = "ClientStreaming",
                OpenedAt = DateTime.UtcNow,
                StreamCall = streamCall,
                RequestStream = requestStream,
                WriteAsyncMethod = writeAsyncMethod,
                CompleteAsyncMethod = completeAsyncMethod,
                RequestMessageType = requestMessageType,
                JsonConverter = _jsonConverter,
                ResponseAsyncProp = responseAsyncProp,
            };
        }

        _streams[streamId] = ctx;
        _logger.LogInformation("Stream opened: {StreamId} ({RpcType})", streamId, ctx.RpcType);

        return streamId;
    }

    /// <summary>열린 스트림에 메시지 1건을 씁니다.</summary>
    public async Task WriteMessageAsync(string streamId, string messageJson)
    {
        if (!_streams.TryGetValue(streamId, out var ctx))
            throw new InvalidOperationException($"Stream '{streamId}' not found");

        try
        {
            var message = await ctx.JsonConverter.JsonToMessageAsync(messageJson, ctx.RequestMessageType);
            await (Task)ctx.WriteAsyncMethod.Invoke(ctx.RequestStream, [message])!;
            ctx.MessagesSent++;
        }
        catch
        {
            _streams.TryRemove(streamId, out _);
            throw;
        }

        _logger.LogDebug("Stream {StreamId}: message #{Count} sent", streamId, ctx.MessagesSent);
    }

    /// <summary>스트림을 닫고 서버 응답을 반환합니다.</summary>
    public async Task<StreamCloseResult> CloseStreamAsync(string streamId)
    {
        if (!_streams.TryRemove(streamId, out var ctx))
            throw new InvalidOperationException($"Stream '{streamId}' not found");

        try
        {
            await (Task)ctx.CompleteAsyncMethod.Invoke(ctx.RequestStream, [])!;

            if (ctx.RpcType == "ClientStreaming" && ctx.ResponseAsyncProp != null)
            {
                dynamic responseTask = ctx.ResponseAsyncProp.GetValue(ctx.StreamCall)!;
                var response = await responseTask;
                return new StreamCloseResult
                {
                    IsSuccess = true,
                    Response = ctx.JsonConverter.MessageToJson((IMessage)response)
                };
            }

            if (ctx.RpcType == "BidirectionalStreaming" && ctx.ReceiveTask != null)
            {
                await ctx.ReceiveTask;
                return new StreamCloseResult { IsSuccess = true, Messages = ctx.ReceivedMessages.ToList() };
            }

            return new StreamCloseResult { IsSuccess = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing stream {StreamId}", streamId);
            return new StreamCloseResult { IsSuccess = false, Error = ex.Message };
        }
    }

    /// <summary>해당 streamId의 스트림이 현재 열려있는지 확인합니다.</summary>
    public bool IsStreamOpen(string streamId) => _streams.ContainsKey(streamId);

    public IReadOnlyList<OpenLocalStreamInfo> SnapshotOpenStreams()
        => _streams.Values
            .Select(ctx => new OpenLocalStreamInfo
            {
                StreamId = ctx.StreamId,
                SessionId = ctx.SessionId,
                ServiceName = ctx.ServiceName,
                MethodName = ctx.MethodName,
                RpcType = ctx.RpcType,
                OpenedAt = ctx.OpenedAt,
                MessagesSent = ctx.MessagesSent,
                ReceivedCount = ctx.ReceivedMessages.Count
            })
            .OrderByDescending(info => info.OpenedAt)
            .ToList();

    // ── 공통 헬퍼 ────────────────────────────────────────────────────────────

    private static CallOptions BuildCallOptions(GrpcRequestPayload payload, bool noDeadline = false)
    {
        var metadata = new Metadata();
        if (!string.IsNullOrWhiteSpace(payload.SessionId))
            metadata.Add(GrpcRequestPayload.SessionIdMetadataKey, payload.SessionId);
        if (payload.Metadata != null)
        {
            foreach (var kvp in payload.Metadata)
                metadata.Add(kvp.Key, kvp.Value);
        }
        var deadline = noDeadline ? (DateTime?)null : DateTime.UtcNow.AddSeconds(payload.TimeoutSeconds);
        return new CallOptions(metadata, deadline);
    }

    private static MethodInfo? FindMethod(Type clientType, string methodName, int paramCount)
    {
        return clientType.GetMethods()
            .Where(m => m.Name == methodName)
            .FirstOrDefault(m => m.GetParameters().Length == paramCount)
            ?? clientType.GetMethods().FirstOrDefault(m => m.Name == methodName);
    }
}
