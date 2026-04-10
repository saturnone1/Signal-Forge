using System.Reflection;
using Grpc.Core;

namespace GrpcWorkbench.Grpc;

/// <summary>
/// gRPC 서비스 클라이언트 타입을 동적으로 찾는 유틸리티입니다.
/// Proto 파일의 네임스페이스와 관계없이 서비스/메시지 타입을 검색합니다.
/// </summary>
public interface IGrpcServiceClientFinder
{
    Type? FindServiceClientType(Assembly assembly, string serviceName);
    Type? FindMessageType(Assembly assembly, string messageName);
}

public class GrpcServiceClientFinder : IGrpcServiceClientFinder
{
    private readonly ILogger<GrpcServiceClientFinder> _logger;

    public GrpcServiceClientFinder(ILogger<GrpcServiceClientFinder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 어셈블리에서 서비스 이름에 해당하는 gRPC 클라이언트 타입을 찾습니다.
    /// 정확한 이름 → 대소문자 무시 → 부분 일치 순으로 검색합니다.
    /// </summary>
    public Type? FindServiceClientType(Assembly assembly, string serviceName)
    {
        var lastName = serviceName.Split('.').Last();

        var exactMatch = assembly.GetTypes()
            .FirstOrDefault(t => t.Name == $"{lastName}Client");

        if (exactMatch != null)
            return exactMatch;

        var allClients = assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Client"))
            .ToList();

        if (allClients.Count == 0)
        {
            _logger.LogWarning("No client types found in assembly for service '{ServiceName}'", serviceName);
            return null;
        }

        var fuzzyMatch = allClients.FirstOrDefault(t =>
            t.Name.Equals($"{lastName}Client", StringComparison.OrdinalIgnoreCase));

        if (fuzzyMatch != null)
            return fuzzyMatch;

        var partialMatch = allClients.FirstOrDefault(t =>
            t.Name.Contains(lastName, StringComparison.OrdinalIgnoreCase));

        if (partialMatch != null)
            return partialMatch;

        _logger.LogError("Cannot find any matching client for service '{ServiceName}'", lastName);
        return null;
    }

    /// <summary>
    /// 어셈블리에서 메시지 이름에 해당하는 Protobuf 메시지 타입을 찾습니다.
    /// </summary>
    public Type? FindMessageType(Assembly assembly, string messageName)
    {
        var lastName = messageName.Split('.').Last();

        var messageType = assembly.GetTypes()
            .FirstOrDefault(t => t.Name == lastName &&
                                 typeof(Google.Protobuf.IMessage).IsAssignableFrom(t))
            ?? assembly.GetTypes()
                .FirstOrDefault(t => t.Name.Equals(lastName, StringComparison.OrdinalIgnoreCase) &&
                                     typeof(Google.Protobuf.IMessage).IsAssignableFrom(t));

        if (messageType == null)
            _logger.LogWarning("Message type '{MessageName}' not found in assembly", lastName);

        return messageType;
    }
}
