using Google.Protobuf;
using Grpc.Core;
using GrpcWorkbench.Grpc;
using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Models.Session;
using System.Diagnostics;
using System.Reflection;

namespace GrpcWorkbench.Services;

public interface IStreamingGrpcService
{
    Task ExecuteServerStreamingAsync(GrpcRequestPayload payload, GrpcSession session, 
        Func<string, Task> onMessageReceived, Func<Exception?, Task> onCompleted);

    Task ExecuteClientStreamingAsync(GrpcRequestPayload payload, GrpcSession session, 
        List<string> requestMessages, int intervalMs, Func<string, Task> onResponseReceived, Func<Exception?, Task> onCompleted);

    Task ExecuteBidirectionalStreamingAsync(GrpcRequestPayload payload, GrpcSession session,
        List<string> requestMessages, int intervalMs, Func<string, Task> onMessageReceived, Func<Exception?, Task> onCompleted);
}

public class StreamingGrpcService : IStreamingGrpcService
{
    private readonly IGrpcChannelProvider _channelProvider;
    private readonly IJsonMessageConverter _jsonConverter;
    private readonly IGrpcServiceClientFinder _clientFinder;
    private readonly IDynamicProtoCompiler _protoCompiler;
    private readonly ILogger<StreamingGrpcService> _logger;

    public StreamingGrpcService(
        IGrpcChannelProvider channelProvider,
        IJsonMessageConverter jsonConverter,
        IGrpcServiceClientFinder clientFinder,
        IDynamicProtoCompiler protoCompiler,
        ILogger<StreamingGrpcService> logger)
    {
        _channelProvider = channelProvider;
        _jsonConverter = jsonConverter;
        _clientFinder = clientFinder;
        _protoCompiler = protoCompiler;
        _logger = logger;
    }

    public async Task ExecuteServerStreamingAsync(GrpcRequestPayload payload, GrpcSession session,
        Func<string, Task> onMessageReceived, Func<Exception?, Task> onCompleted)
    {
        try
        {
            var assembly = await LoadAssemblyFromProtoAsync(session.ProtoContent ?? []);
            var channel = await _channelProvider.GetChannelAsync(session);

            var metadata = new Metadata();
            if (payload.Metadata != null)
            {
                foreach (var kvp in payload.Metadata)
                    metadata.Add(kvp.Key, kvp.Value);
            }

            var callOptions = new CallOptions(metadata, DateTime.UtcNow.AddSeconds(payload.TimeoutSeconds));

            // 동적으로 클라이언트 타입 찾기
            var clientType = FindServiceClientType(assembly, payload.ServiceName);

            if (clientType == null)
                throw new InvalidOperationException($"Service client not found for '{payload.ServiceName}'");

            var clientInstance = Activator.CreateInstance(clientType, channel);
            var methodInfo = clientType.GetMethods()
                .Where(m => m.Name == payload.MethodName)
                .FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    return p.Length == 2 && p[1].ParameterType == typeof(CallOptions);
                })
                ?? clientType.GetMethods().FirstOrDefault(m => m.Name == payload.MethodName);

            if (methodInfo == null)
                throw new InvalidOperationException($"Method '{payload.MethodName}' not found");

            // 요청 메시지 타입을 메서드 파라미터에서 추출
            var methodParams = methodInfo.GetParameters();
            var requestMessageType = methodParams.FirstOrDefault()?.ParameterType;
            if (requestMessageType == null || !typeof(IMessage).IsAssignableFrom(requestMessageType))
                throw new InvalidOperationException($"Could not determine request message type for method {payload.MethodName}");

            var requestMessage = await _jsonConverter.JsonToMessageAsync(payload.RequestJson, requestMessageType);

            // Server streaming 호출
            var streamCallResult = methodInfo.Invoke(clientInstance, [requestMessage, callOptions])!;

            // ResponseStream을 reflection으로 가져오기
            var responseStreamProp = streamCallResult.GetType().GetProperty("ResponseStream");
            if (responseStreamProp == null)
                throw new InvalidOperationException("Could not find ResponseStream on streaming call");

            var responseStream = responseStreamProp.GetValue(streamCallResult)!;
            var moveNextMethod = responseStream.GetType().GetMethod("MoveNext", [typeof(CancellationToken)]);
            var currentProperty = responseStream.GetType().GetProperty("Current");

            if (moveNextMethod == null || currentProperty == null)
                throw new InvalidOperationException("Could not find MoveNext/Current on ResponseStream");

            while (true)
            {
                var moveNextTask = (Task<bool>)moveNextMethod.Invoke(responseStream, [CancellationToken.None])!;
                var hasNext = await moveNextTask;
                if (!hasNext) break;

                var current = (IMessage)currentProperty.GetValue(responseStream)!;
                var responseJson = _jsonConverter.MessageToJson(current);
                await onMessageReceived(responseJson);
            }

            await onCompleted(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server streaming failed");
            await onCompleted(ex);
        }
    }

    private Type? FindServiceClientType(System.Reflection.Assembly assembly, string serviceName)
    {
        return _clientFinder.FindServiceClientType(assembly, serviceName);
    }

    public async Task ExecuteClientStreamingAsync(GrpcRequestPayload payload, GrpcSession session,
        List<string> requestMessages, int intervalMs, Func<string, Task> onResponseReceived, Func<Exception?, Task> onCompleted)
    {
        try
        {
            var assembly = await LoadAssemblyFromProtoAsync(session.ProtoContent ?? []);
            var channel = await _channelProvider.GetChannelAsync(session);

            var metadata = new Metadata();
            if (payload.Metadata != null)
            {
                foreach (var kvp in payload.Metadata)
                    metadata.Add(kvp.Key, kvp.Value);
            }

            var callOptions = new CallOptions(metadata, DateTime.UtcNow.AddSeconds(payload.TimeoutSeconds));

            // 동적으로 클라이언트 타입 찾기
            var clientType = FindServiceClientType(assembly, payload.ServiceName);

            if (clientType == null)
            {
                var allTypes = string.Join(", ", assembly.GetTypes().Select(t => t.FullName).Take(20));
                throw new InvalidOperationException($"Service client for '{payload.ServiceName}' not found. Assembly types: {allTypes}");
            }

            var clientInstance = Activator.CreateInstance(clientType, channel);
            if (clientInstance == null)
                throw new InvalidOperationException($"Failed to create client instance of type {clientType.FullName}");

            var methodInfo = clientType.GetMethods()
                .Where(m => m.Name == payload.MethodName)
                .FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    return p.Length == 1 && p[0].ParameterType == typeof(CallOptions);
                })
                ?? clientType.GetMethods().FirstOrDefault(m => m.Name == payload.MethodName);

            if (methodInfo == null)
                throw new InvalidOperationException($"Method '{payload.MethodName}' not found on {clientType.FullName}");

            // 메서드의 입력 메시지 타입을 reflection으로 추출
            var returnType = methodInfo.ReturnType;
            Type? requestMessageType = null;

            if (returnType.IsGenericType)
            {
                var genericArgs = returnType.GetGenericArguments();
                if (genericArgs.Length >= 1)
                {
                    requestMessageType = genericArgs[0];
                }
            }

            if (requestMessageType == null)
            {
                throw new InvalidOperationException($"Could not determine request message type for method {payload.MethodName}");
            }

            // Client streaming 호출
            var streamCallResult = methodInfo.Invoke(clientInstance, [callOptions])!;

            // RequestStream을 reflection으로 가져오기
            var requestStreamProp = streamCallResult.GetType().GetProperty("RequestStream");
            var responseAsyncProp = streamCallResult.GetType().GetProperty("ResponseAsync");

            if (requestStreamProp == null || responseAsyncProp == null)
                throw new InvalidOperationException("Could not find RequestStream or ResponseAsync on streaming call");

            var requestStream = requestStreamProp.GetValue(streamCallResult)!;
            var writeAsyncMethod = requestStream.GetType().GetMethod("WriteAsync", [requestMessageType]);
            var completeAsyncMethod = requestStream.GetType().GetMethod("CompleteAsync");

            if (writeAsyncMethod == null || completeAsyncMethod == null)
                throw new InvalidOperationException("Could not find WriteAsync/CompleteAsync on RequestStream");

            // 요청 메시지 전송
            foreach (var requestJson in requestMessages)
            {
                var message = await _jsonConverter.JsonToMessageAsync(requestJson, requestMessageType);
                var writeTask = (Task)writeAsyncMethod.Invoke(requestStream, [message])!;
                await writeTask;
                if (intervalMs > 0) await Task.Delay(intervalMs);
            }

            var completeTask = (Task)completeAsyncMethod.Invoke(requestStream, [])!;
            await completeTask;

            // 응답 대기
            dynamic responseTask = responseAsyncProp.GetValue(streamCallResult)!;
            var response = await responseTask;

            var responseJson = _jsonConverter.MessageToJson((IMessage)response);
            await onResponseReceived(responseJson);
            await onCompleted(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client streaming failed");
            await onCompleted(ex);
        }
    }

    public async Task ExecuteBidirectionalStreamingAsync(GrpcRequestPayload payload, GrpcSession session,
        List<string> requestMessages, int intervalMs, Func<string, Task> onMessageReceived, Func<Exception?, Task> onCompleted)
    {
        try
        {
            var assembly = await LoadAssemblyFromProtoAsync(session.ProtoContent ?? []);
            var channel = await _channelProvider.GetChannelAsync(session);

            var metadata = new Metadata();
            if (payload.Metadata != null)
            {
                foreach (var kvp in payload.Metadata)
                    metadata.Add(kvp.Key, kvp.Value);
            }

            var callOptions = new CallOptions(metadata, DateTime.UtcNow.AddSeconds(payload.TimeoutSeconds));

            // 동적으로 클라이언트 타입 찾기
            var clientType = FindServiceClientType(assembly, payload.ServiceName);

            if (clientType == null)
            {
                var allTypes = string.Join(", ", assembly.GetTypes().Select(t => t.FullName).Take(20));
                throw new InvalidOperationException($"Service client for '{payload.ServiceName}' not found. Assembly types: {allTypes}");
            }

            var clientInstance = Activator.CreateInstance(clientType, channel);
            if (clientInstance == null)
                throw new InvalidOperationException($"Failed to create client instance of type {clientType.FullName}");

            var methodInfo = clientType.GetMethods()
                .Where(m => m.Name == payload.MethodName)
                .FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    return p.Length == 1 && p[0].ParameterType == typeof(CallOptions);
                })
                ?? clientType.GetMethods().FirstOrDefault(m => m.Name == payload.MethodName);

            if (methodInfo == null)
                throw new InvalidOperationException($"Method '{payload.MethodName}' not found on {clientType.FullName}");

            // 메서드의 입력 메시지 타입을 reflection으로 추출
            var returnType = methodInfo.ReturnType;
            Type? requestMessageType = null;

            if (returnType.IsGenericType)
            {
                var genericArgs = returnType.GetGenericArguments();
                if (genericArgs.Length >= 1)
                {
                    requestMessageType = genericArgs[0];
                }
            }

            if (requestMessageType == null)
            {
                throw new InvalidOperationException($"Could not determine request message type for method {payload.MethodName}");
            }

            dynamic? streamCall = methodInfo.Invoke(clientInstance, [callOptions]);
            if (streamCall == null)
                throw new InvalidOperationException($"Failed to invoke streaming method");

            // RequestStream과 ResponseStream을 reflection으로 가져오기
            var streamCallObj = (object)streamCall;
            var requestStreamProp = streamCallObj.GetType().GetProperty("RequestStream");
            var responseStreamProp = streamCallObj.GetType().GetProperty("ResponseStream");

            if (requestStreamProp == null || responseStreamProp == null)
                throw new InvalidOperationException("Could not find RequestStream or ResponseStream on streaming call");

            var requestStream = requestStreamProp.GetValue(streamCallObj)!;
            var responseStream = responseStreamProp.GetValue(streamCallObj)!;

            // WriteAsync / CompleteAsync / MoveNext / Current 메서드/프로퍼티 찾기
            var writeAsyncMethod = requestStream.GetType().GetMethod("WriteAsync", [requestMessageType]);
            var completeAsyncMethod = requestStream.GetType().GetMethod("CompleteAsync");
            var moveNextMethod = responseStream.GetType().GetMethod("MoveNext", [typeof(CancellationToken)]);
            var currentProperty = responseStream.GetType().GetProperty("Current");

            if (writeAsyncMethod == null || completeAsyncMethod == null)
                throw new InvalidOperationException("Could not find WriteAsync/CompleteAsync on RequestStream");
            if (moveNextMethod == null || currentProperty == null)
                throw new InvalidOperationException("Could not find MoveNext/Current on ResponseStream");

            // 요청/응답을 동시에 처리
            var sendTask = Task.Run(async () =>
            {
                try
                {
                    foreach (var requestJson in requestMessages)
                    {
                        var message = await _jsonConverter.JsonToMessageAsync(requestJson, requestMessageType);
                        var writeTask = (Task)writeAsyncMethod.Invoke(requestStream, [message])!;
                        await writeTask;
                        if (intervalMs > 0) await Task.Delay(intervalMs);
                    }
                    var completeTask = (Task)completeAsyncMethod.Invoke(requestStream, [])!;
                    await completeTask;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending messages");
                }
            });

            var receiveTask = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var moveNextTask = (Task<bool>)moveNextMethod.Invoke(responseStream, [CancellationToken.None])!;
                        var hasNext = await moveNextTask;
                        if (!hasNext) break;

                        var response = currentProperty.GetValue(responseStream);
                        var responseJson = _jsonConverter.MessageToJson((IMessage)response!);
                        await onMessageReceived(responseJson);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error receiving messages");
                }
            });

            await Task.WhenAll(sendTask, receiveTask);
            await onCompleted(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bidirectional streaming failed");
            await onCompleted(ex);
        }
    }

    private async Task<Assembly> LoadAssemblyFromProtoAsync(byte[] protoContent)
    {
        return await _protoCompiler.CompileProtoToAssemblyAsync(protoContent);
    }
}
