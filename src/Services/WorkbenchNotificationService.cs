using System.Collections.Concurrent;

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
    public event Action<IncomingCallStartedEvent>? CallStarted;
    public event Action<IncomingStreamMessageEvent>? StreamMessageReceived;
    public event Action<IncomingCallEndedEvent>? CallEnded;

    public void NotifyCallStarted(IncomingCallStartedEvent e) => CallStarted?.Invoke(e);
    public void NotifyStreamMessage(IncomingStreamMessageEvent e) => StreamMessageReceived?.Invoke(e);
    public void NotifyCallEnded(IncomingCallEndedEvent e) => CallEnded?.Invoke(e);

    // ── 서비스 레지스트리 ──────────────────────────────────────────────────────
    // ProtoLoader가 파싱한 서비스/메서드 메타데이터를 저장.
    // 미들웨어가 수신 시 정확한 RPC 타입을 조회하는 데 사용합니다.
    private readonly Dictionary<string, Dictionary<string, string>> _rpcTypeMap = new(StringComparer.OrdinalIgnoreCase);

    // 수신 미들웨어가 프레임마다 호출하므로 methodName→메시지 Type을 캐시한다.
    // (null도 캐시 = negative caching) proto 재등록 시 새 어셈블리 기준으로 재해석되도록 비운다.
    private readonly ConcurrentDictionary<string, Type?> _msgTypeCache = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterServices(IEnumerable<GrpcWorkbench.Models.Grpc.ServiceMetadata> services)
    {
        lock (_rpcTypeMap)
        {
            _rpcTypeMap.Clear();
            foreach (var svc in services)
                _rpcTypeMap[svc.ServiceName] = svc.Methods.ToDictionary(
                    m => m.MethodName, m => m.RpcType, StringComparer.OrdinalIgnoreCase);
        }
        _msgTypeCache.Clear();
    }

    /// <summary>
    /// methodName에 대한 protobuf 메시지 Type을 캐시에서 반환하고,
    /// 없으면 <paramref name="resolver"/>로 1회 해석 후 캐시합니다(미해결 null도 캐시).
    /// </summary>
    public Type? GetOrResolveRequestType(string methodName, Func<string, Type?> resolver)
        => _msgTypeCache.GetOrAdd(methodName, resolver);

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
}
