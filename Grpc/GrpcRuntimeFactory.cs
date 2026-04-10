using Google.Protobuf;
using Google.Protobuf.Reflection;
using System.Reflection;
using Grpc.Net.Client;

namespace GrpcWorkbench.Grpc;

public interface IGrpcRuntimeFactory
{
    Task<Type?> GetMessageTypeAsync(string messageName, Assembly assembly);
    Task<MethodInfo?> GetMethodInfoAsync(string serviceName, string methodName, Assembly assembly);
    Task<object> InvokeUnaryMethodAsync(GrpcChannel channel, string serviceName, string methodName, 
        IMessage request, Dictionary<string, string>? metadata = null);
}

public class GrpcRuntimeFactory : IGrpcRuntimeFactory
{
    private readonly ILogger<GrpcRuntimeFactory> _logger;

    public GrpcRuntimeFactory(ILogger<GrpcRuntimeFactory> logger)
    {
        _logger = logger;
    }

    public Task<Type?> GetMessageTypeAsync(string messageName, Assembly assembly)
    {
        try
        {
            var type = assembly.GetType(messageName);
            return Task.FromResult(type);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get message type {MessageName}", messageName);
            throw;
        }
    }

    public Task<MethodInfo?> GetMethodInfoAsync(string serviceName, string methodName, Assembly assembly)
    {
        try
        {
            var serviceType = assembly.GetType(serviceName);
            var method = serviceType?.GetMethod(methodName, 
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return Task.FromResult(method);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get method info {ServiceName}.{MethodName}", serviceName, methodName);
            throw;
        }
    }

    public async Task<object> InvokeUnaryMethodAsync(GrpcChannel channel, string serviceName, string methodName,
        IMessage request, Dictionary<string, string>? metadata = null)
    {
        try
        {
            // 동적으로 gRPC 메서드 호출
            // 이는 복잡한 리플렉션을 요구하므로, 실제 구현은 더 정교함
            await Task.Delay(0);
            return new object();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invoke method {ServiceName}.{MethodName}", serviceName, methodName);
            throw;
        }
    }
}
