using Google.Protobuf;
using Grpc.Core;
using GrpcWorkbench.Grpc;
using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Models.Session;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace GrpcWorkbench.Services;

public interface IUnaryGrpcService
{
    Task<GrpcResponseData> ExecuteUnaryCallAsync(GrpcRequestPayload payload, GrpcSession session);
}

public class UnaryGrpcService : IUnaryGrpcService
{
    private readonly IGrpcChannelProvider _channelProvider;
    private readonly IJsonMessageConverter _jsonConverter;
    private readonly IDynamicProtoCompiler _protoCompiler;
    private readonly IGrpcServiceClientFinder _clientFinder;
    private readonly ILogger<UnaryGrpcService> _logger;

    public UnaryGrpcService(
        IGrpcChannelProvider channelProvider,
        IJsonMessageConverter jsonConverter,
        IDynamicProtoCompiler protoCompiler,
        IGrpcServiceClientFinder clientFinder,
        ILogger<UnaryGrpcService> logger)
    {
        _channelProvider = channelProvider;
        _jsonConverter = jsonConverter;
        _protoCompiler = protoCompiler;
        _clientFinder = clientFinder;
        _logger = logger;
    }

    public async Task<GrpcResponseData> ExecuteUnaryCallAsync(GrpcRequestPayload payload, GrpcSession session)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = new GrpcResponseData();

        try
        {
            // Proto 파일을 동적으로 컴파일하여 어셈블리 로드
            var assembly = await _protoCompiler.CompileProtoToAssemblyAsync(session.ProtoContent ?? []);

            // 동적으로 클라이언트 타입 찾기
            var clientType = _clientFinder.FindServiceClientType(assembly, payload.ServiceName);
            if (clientType == null)
            {
                var allTypes = string.Join(", ", assembly.GetTypes().Select(t => t.FullName).Take(20));
                throw new InvalidOperationException($"Service client for '{payload.ServiceName}' not found. Assembly types: {allTypes}");
            }

            // 메서드 찾기 (CallOptions를 받는 오버로드 선택)
            var methodInfo = clientType.GetMethods()
                .Where(m => m.Name == payload.MethodName)
                .FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    return p.Length == 2 && p[1].ParameterType == typeof(CallOptions);
                })
                ?? clientType.GetMethods().FirstOrDefault(m => m.Name == payload.MethodName);

            if (methodInfo == null)
                throw new InvalidOperationException($"Method '{payload.MethodName}' not found on {clientType.FullName}");

            // 요청 메시지 타입을 메서드 파라미터에서 추출
            var methodParams = methodInfo.GetParameters();
            var requestMessageType = methodParams.FirstOrDefault()?.ParameterType;
            if (requestMessageType == null || !typeof(IMessage).IsAssignableFrom(requestMessageType))
                throw new InvalidOperationException($"Could not determine request message type for method {payload.MethodName}");

            _logger.LogInformation($"Request message type: {requestMessageType.FullName}");

            // JSON을 Protobuf 메시지로 변환
            var requestMessage = await _jsonConverter.JsonToMessageAsync(payload.RequestJson, requestMessageType);

            // gRPC 채널 획득
            var channel = await _channelProvider.GetChannelAsync(session);

            // 메타데이터 작성
            var metadata = new Metadata();
            if (payload.Metadata != null)
            {
                foreach (var kvp in payload.Metadata)
                {
                    metadata.Add(kvp.Key, kvp.Value);
                }
            }

            // CallOptions 구성
            var callOptions = new CallOptions(
                metadata,
                DateTime.UtcNow.AddSeconds(payload.TimeoutSeconds)
            );

            // 클라이언트 인스턴스 생성
            var clientInstance = Activator.CreateInstance(clientType, channel);

            dynamic? result = null;

            if (methodInfo.ReturnType.IsGenericType && methodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var task = (Task)methodInfo.Invoke(clientInstance, [requestMessage, callOptions])!;
                await task;
                result = task.GetType().GetProperty("Result")?.GetValue(task);
            }
            else
            {
                result = methodInfo.Invoke(clientInstance, [requestMessage, callOptions]);
            }

            // 응답을 JSON으로 변환
            if (result is IMessage message)
            {
                response.ResponseJson = _jsonConverter.MessageToJson(message);
            }

            response.IsSuccess = true;
            _logger.LogInformation("Unary call succeeded: {ServiceName}.{MethodName}", 
                payload.ServiceName, payload.MethodName);
        }
        catch (Exception ex)
        {
            response.IsSuccess = false;
            response.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Unary call failed: {ServiceName}.{MethodName}", 
                payload.ServiceName, payload.MethodName);
        }
        finally
        {
            stopwatch.Stop();
            response.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        }

        return response;
    }
}
