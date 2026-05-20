using Google.Protobuf;
using GrpcWorkbench.Grpc;
using GrpcWorkbench.Models.Grpc;
using System.Collections.Concurrent;
using System.Reflection;

namespace GrpcWorkbench.Services;

public record IncomingCallStartedEvent(
    string CallId,
    string Service,
    string Method,
    string Type,
    string Req);

public record IncomingStreamMessageEvent(
    string CallId,
    int FrameIndex,
    string Data);

public record IncomingCallEndedEvent(
    string CallId,
    string Res);

/// <summary>
/// 이 인스턴스의 gRPC 서버(UDS)로 들어오는 요청을 Blazor 컴포넌트에 알립니다.
/// GenericGrpcReceiverMiddleware → WorkbenchNotificationService → Workbench.razor
/// </summary>
public class WorkbenchNotificationService
{
    private sealed class InboundResponseStreamHandle
    {
        public required string Service { get; init; }
        public required string Method { get; init; }
        public required Func<byte[], Task> WriteAsync { get; init; }
    }

    public event Action<IncomingCallStartedEvent>? CallStarted;
    public event Action<IncomingStreamMessageEvent>? StreamMessageReceived;
    public event Action<IncomingCallEndedEvent>? CallEnded;

    private readonly IJsonMessageConverter _jsonConverter;
    private readonly ILogger<WorkbenchNotificationService> _logger;

    public void NotifyCallStarted(IncomingCallStartedEvent e) => CallStarted?.Invoke(e);
    public void NotifyStreamMessage(IncomingStreamMessageEvent e) => StreamMessageReceived?.Invoke(e);
    public void NotifyCallEnded(IncomingCallEndedEvent e) => CallEnded?.Invoke(e);

    // ── 서비스 레지스트리 ──────────────────────────────────────────────────────
    // ProtoLoader가 파싱한 서비스/메서드 메타데이터를 저장.
    // 미들웨어가 수신 시 정확한 RPC 타입을 조회하는 데 사용합니다.
    private readonly Dictionary<string, Dictionary<string, string>> _rpcTypeMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, MethodMetadata>> _methodMap = new(StringComparer.OrdinalIgnoreCase);

    // 수신 미들웨어가 프레임마다 호출하므로 methodName→메시지 Type을 캐시한다.
    // (null도 캐시 = negative caching) proto 재등록 시 새 어셈블리 기준으로 재해석되도록 비운다.
    private readonly ConcurrentDictionary<string, Type?> _msgTypeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Type?> _responseTypeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InboundResponseStreamHandle> _inboundResponseStreams = new(StringComparer.OrdinalIgnoreCase);

    public WorkbenchNotificationService(IJsonMessageConverter jsonConverter, ILogger<WorkbenchNotificationService> logger)
    {
        _jsonConverter = jsonConverter;
        _logger = logger;
    }

    public void RegisterServices(IEnumerable<GrpcWorkbench.Models.Grpc.ServiceMetadata> services)
    {
        lock (_rpcTypeMap)
        {
            _rpcTypeMap.Clear();
            _methodMap.Clear();
            foreach (var svc in services)
            {
                _rpcTypeMap[svc.ServiceName] = svc.Methods.ToDictionary(
                    m => m.MethodName, m => m.RpcType, StringComparer.OrdinalIgnoreCase);
                _methodMap[svc.ServiceName] = svc.Methods.ToDictionary(
                    m => m.MethodName, m => m, StringComparer.OrdinalIgnoreCase);
            }
        }
        _msgTypeCache.Clear();
        _responseTypeCache.Clear();
    }

    /// <summary>
    /// methodName에 대한 protobuf 메시지 Type을 캐시에서 반환하고,
    /// 없으면 <paramref name="resolver"/>로 1회 해석 후 캐시합니다(미해결 null도 캐시).
    /// </summary>
    public Type? GetOrResolveRequestType(string methodName, Func<string, Type?> resolver)
        => _msgTypeCache.GetOrAdd(methodName, resolver);

    public MethodMetadata? GetMethodMetadata(string serviceName, string methodName)
    {
        lock (_rpcTypeMap)
        {
            if (_methodMap.TryGetValue(serviceName, out var methods) &&
                methods.TryGetValue(methodName, out var method))
                return method;
        }

        return null;
    }

    public bool CanWriteInboundResponse(string callId)
        => _inboundResponseStreams.ContainsKey(callId);

    public void RegisterInboundResponseStream(string callId, string serviceName, string methodName, Func<byte[], Task> writeAsync)
    {
        _inboundResponseStreams[callId] = new InboundResponseStreamHandle
        {
            Service = serviceName,
            Method = methodName,
            WriteAsync = writeAsync
        };
    }

    public void UnregisterInboundResponseStream(string callId)
    {
        _inboundResponseStreams.TryRemove(callId, out _);
    }

    public async Task SendInboundResponseAsync(string callId, string json)
    {
        if (!_inboundResponseStreams.TryGetValue(callId, out var stream))
            throw new InvalidOperationException("활성 inbound stream을 찾을 수 없습니다.");

        var method = GetMethodMetadata(stream.Service, stream.Method)
            ?? throw new InvalidOperationException($"메서드 메타데이터를 찾을 수 없습니다: {stream.Service}.{stream.Method}");

        var typeCacheKey = $"{stream.Service}/{stream.Method}";
        var messageType = _responseTypeCache.GetOrAdd(typeCacheKey, _ => ResolveMessageType(method.OutputType));
        if (messageType == null)
            throw new InvalidOperationException($"응답 메시지 타입을 찾을 수 없습니다: {method.OutputType}");

        var message = await _jsonConverter.JsonToMessageAsync(json, messageType);
        await stream.WriteAsync(message.ToByteArray());

        _logger.LogInformation("Inbound response sent: {CallId} {Service}.{Method}", callId, stream.Service, stream.Method);
    }

    /// <summary>
    /// proto 메타데이터에서 RPC 타입을 조회합니다.
    /// 메타데이터가 없으면 메서드명 "Stream" 접두사 heuristic으로 폴백합니다.
    /// </summary>
    public string GetRpcType(string serviceName, string methodName)
    {
        lock (_rpcTypeMap)
        {
            if (_rpcTypeMap.TryGetValue(serviceName, out var methods) &&
                methods.TryGetValue(methodName, out var rpcType))
                return rpcType;
        }
        // 폴백: proto 메타데이터 없을 때 이름 기반 heuristic
        return methodName.StartsWith("Stream", StringComparison.OrdinalIgnoreCase)
            ? "BidirectionalStreaming" : "Unary";
    }

    private static Type? ResolveMessageType(string? protoTypeName)
    {
        var normalized = (protoTypeName ?? string.Empty).Trim().TrimStart('.');
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        var simpleName = normalized.Split('.').Last();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            }

            var match = types.FirstOrDefault(t =>
                typeof(IMessage).IsAssignableFrom(t) &&
                (string.Equals(t.Name, simpleName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t.Name, normalized, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t.FullName, normalized, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t.FullName?.Split('.').Last(), simpleName, StringComparison.OrdinalIgnoreCase)));

            if (match != null)
                return match;
        }

        return null;
    }
}
