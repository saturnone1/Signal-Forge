using Grpc.Net.Client;
using ProtobufServiceDescriptor = Google.Protobuf.Reflection.ServiceDescriptor;

namespace GrpcWorkbench.Grpc;

public interface IReflectionHelper
{
    Task<ProtobufServiceDescriptor?> GetServiceDescriptorAsync(GrpcChannel channel, string serviceName);
    Task<List<string>> GetAvailableServicesAsync(GrpcChannel channel);
}

public class ReflectionHelper : IReflectionHelper
{
    private readonly ILogger<ReflectionHelper> _logger;

    public ReflectionHelper(ILogger<ReflectionHelper> logger)
    {
        _logger = logger;
    }

    public async Task<ProtobufServiceDescriptor?> GetServiceDescriptorAsync(GrpcChannel channel, string serviceName)
    {
        try
        {
            // gRPC reflection을 사용하여 서비스 정보 조회
            // 기본 구현 - 실제로는 reflection API 호출 필요
            await Task.Delay(0);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get service descriptor for {ServiceName}", serviceName);
            throw;
        }
    }

    public async Task<List<string>> GetAvailableServicesAsync(GrpcChannel channel)
    {
        try
        {
            var services = new List<string>();
            // gRPC reflection을 사용하여 사용 가능한 서비스 목록 조회
            await Task.Delay(0);
            return services;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get available services");
            throw;
        }
    }
}
