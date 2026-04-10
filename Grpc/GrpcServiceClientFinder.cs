using System.Reflection;
using Grpc.Core;

namespace GrpcWorkbench.Grpc;

/// <summary>
/// gRPC 서비스 클라이언트 타입을 동적으로 찾는 유틸리티
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
    /// Proto 파일의 네임스페이스와 관계없이 서비스 클라이언트 타입을 찾습니다.
    /// </summary>
    public Type? FindServiceClientType(Assembly assembly, string serviceName)
    {
        var serviceNameParts = serviceName.Split('.');
        var lastPart = serviceNameParts.Last();

        // 1단계: 정확한 이름으로 찾기 (ServiceNameClient)
        var clientTypes = assembly.GetTypes()
            .Where(t => t.Name == $"{lastPart}Client")
            .ToList();

        if (clientTypes.Count > 0)
        {
            _logger.LogInformation($"Found {clientTypes.Count} client type(s) for service '{lastPart}': {string.Join(", ", clientTypes.Select(t => t.FullName))}");
            return clientTypes.First();
        }

        // 2단계: 모든 Client 타입 조회 및 로깅
        var allClientTypes = assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Client"))
            .ToList();

        if (allClientTypes.Count == 0)
        {
            _logger.LogWarning($"No client types found in assembly. Service: '{serviceName}'");
            return null;
        }

        _logger.LogWarning($"No exact match for '{lastPart}'. Available client types: {string.Join(", ", allClientTypes.Select(t => t.FullName))}");

        // 3단계: 퍼지 매칭 (대소문자 무시)
        var fuzzyMatch = allClientTypes.FirstOrDefault(t => 
            t.Name.Equals($"{lastPart}Client", StringComparison.OrdinalIgnoreCase));

        if (fuzzyMatch != null)
        {
            _logger.LogInformation($"Using fuzzy matched client: {fuzzyMatch.FullName}");
            return fuzzyMatch;
        }

        // 4단계: 서비스명 포함하는 클라이언트 찾기
        var partialMatch = allClientTypes.FirstOrDefault(t => 
            t.Name.Contains(lastPart, StringComparison.OrdinalIgnoreCase));

        if (partialMatch != null)
        {
            _logger.LogInformation($"Using partial matched client: {partialMatch.FullName}");
            return partialMatch;
        }

        _logger.LogError($"Cannot find any matching client for service '{lastPart}'");
        return null;
    }

    /// <summary>
    /// Proto 파일의 네임스페이스와 관계없이 메시지 타입을 찾습니다.
    /// </summary>
    public Type? FindMessageType(Assembly assembly, string messageName)
    {
        var lastPart = messageName.Split('.').Last();

        // IMessage를 구현한 타입 중에서 이름이 일치하는 것 찾기
        var messageType = assembly.GetTypes()
            .FirstOrDefault(t => t.Name == lastPart && 
                                 typeof(Google.Protobuf.IMessage).IsAssignableFrom(t));

        if (messageType != null)
        {
            _logger.LogInformation($"Found message type: {messageType.FullName}");
            return messageType;
        }

        // 대소문자 무시 검색
        messageType = assembly.GetTypes()
            .FirstOrDefault(t => t.Name.Equals(lastPart, StringComparison.OrdinalIgnoreCase) && 
                                 typeof(Google.Protobuf.IMessage).IsAssignableFrom(t));

        if (messageType != null)
        {
            _logger.LogInformation($"Found message type (case-insensitive): {messageType.FullName}");
            return messageType;
        }

        var allMessageTypes = assembly.GetTypes()
            .Where(t => typeof(Google.Protobuf.IMessage).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.FullName)
            .ToList();

        _logger.LogWarning($"Message type '{lastPart}' not found. Available: {string.Join(", ", allMessageTypes)}");
        return null;
    }
}
