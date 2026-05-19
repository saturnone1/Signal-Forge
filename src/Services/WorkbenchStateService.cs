using GrpcWorkbench.Models.Grpc;
using GrpcWorkbench.Models.Session;
using GrpcWorkbench.Models.Ui;
using LogLevel = GrpcWorkbench.Models.Ui.LogLevel;

namespace GrpcWorkbench.Services;

/// <summary>
/// 회로(circuit)와 독립적으로 살아있는 워크벤치 누적 상태 보관자.
/// 미들웨어 알림을 직접 구독해 IncomingCalls / Logs / StreamRecv 등을 누적하고,
/// 1-탭 정책: ClaimActive로 새 클라이언트가 진입하면 이전 클라이언트는 Evicted 통지.
/// 모든 상태 변경은 _lock 안에서 수행, 렌더 측은 Snapshot* 으로 안전 사본을 얻는다.
/// </summary>
public class WorkbenchStateService : IDisposable
{
    private readonly WorkbenchNotificationService _notify;
    private readonly object _lock = new();

    // ── 누적 상태 ──────────────────────────────────────────────────────────
    private readonly List<IncomingCallVm> _incoming = [];
    private readonly List<LogEntry> _logs = [];
    private readonly List<string> _streamRecv = [];

    // ── 발신/선택 상태 ─────────────────────────────────────────────────────
    public GrpcSession? Session { get; private set; }
    public string? StreamId { get; private set; }
    public bool IsStreamOpen => StreamId != null;
    public int SentCount { get; private set; }

    public List<ServiceMetadata> Services { get; private set; } = [];
    public string? SelectedServiceName { get; private set; }
    public string? SelectedMethodName { get; private set; }

    public bool IncomingPaused { get; private set; }

    // 카운트 (lock 없이 빠른 헤더 표시)
    public int IncomingCount { get { lock (_lock) return _incoming.Count; } }
    public int LogCount { get { lock (_lock) return _logs.Count; } }
    public int StreamRecvCount { get { lock (_lock) return _streamRecv.Count; } }

    private const int MaxIncomingCalls = 100;
    private const int MaxFramesPerCall = 1000;
    private const int MaxLogs = 500;
    private const int MaxStreamRecv = 500;

    public event Action? Changed;

    // ── 1-탭 정책 ──────────────────────────────────────────────────────────
    private Guid? _activeClient;
    public event Action? Evicted;

    public WorkbenchStateService(WorkbenchNotificationService notify)
    {
        _notify = notify;
        _notify.CallStarted += OnCallStarted;
        _notify.StreamMessageReceived += OnStreamMessage;
        _notify.CallEnded += OnCallEnded;
    }

    public void Dispose()
    {
        _notify.CallStarted -= OnCallStarted;
        _notify.StreamMessageReceived -= OnStreamMessage;
        _notify.CallEnded -= OnCallEnded;
    }

    // ── 스냅샷 (렌더 측에서 호출) ──────────────────────────────────────────
    // 락 안에서 얕은 사본을 만들어 enumerator 동시 변경 예외를 막는다.
    public IReadOnlyList<IncomingCallVm> SnapshotIncoming()
    {
        lock (_lock) return [.. _incoming];
    }

    public IReadOnlyList<FrameVm> SnapshotFrames(IncomingCallVm call)
    {
        lock (_lock) return [.. call.Frames];
    }

    public IReadOnlyList<LogEntry> SnapshotLogs()
    {
        lock (_lock) return [.. _logs];
    }

    public IReadOnlyList<string> SnapshotStreamRecv()
    {
        lock (_lock) return [.. _streamRecv];
    }

    // ── 미들웨어 알림 핸들러 ───────────────────────────────────────────────
    private void OnCallStarted(IncomingCallStartedEvent e)
    {
        lock (_lock)
        {
            if (_incoming.Count >= MaxIncomingCalls) _incoming.RemoveAt(0);
            _incoming.Add(new IncomingCallVm(e.CallId, e.Service, e.Method, e.Type));
        }
        Changed?.Invoke();
    }

    private void OnStreamMessage(IncomingStreamMessageEvent e)
    {
        lock (_lock)
        {
            var call = _incoming.FirstOrDefault(c => c.CallId == e.CallId);
            if (call == null) return;
            if (call.Frames.Count >= MaxFramesPerCall) call.Frames.RemoveAt(0);
            call.Frames.Add(new FrameVm(e.FrameIndex, e.Data));
        }
        Changed?.Invoke();
    }

    private void OnCallEnded(IncomingCallEndedEvent e)
    {
        lock (_lock)
        {
            var call = _incoming.FirstOrDefault(c => c.CallId == e.CallId);
            if (call != null) call.Result = e.Res;
        }
        Changed?.Invoke();
    }

    // ── UI 호출 ────────────────────────────────────────────────────────────
    public void ClearIncoming()
    {
        lock (_lock) _incoming.Clear();
        Changed?.Invoke();
    }

    public void ClearLogs()
    {
        lock (_lock) _logs.Clear();
        Changed?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        if (IncomingPaused == paused) return;
        IncomingPaused = paused;
        Changed?.Invoke();
    }

    public void AddLog(string text, LogLevel level = LogLevel.Info)
    {
        lock (_lock)
        {
            if (_logs.Count >= MaxLogs) _logs.RemoveAt(0);
            _logs.Add(new LogEntry(DateTime.Now, text, level));
        }
        Changed?.Invoke();
    }

    public void SetSession(GrpcSession? session)
    {
        Session = session;
        if (session == null)
        {
            StreamId = null;
            SentCount = 0;
            lock (_lock) _streamRecv.Clear();
        }
        Changed?.Invoke();
    }

    public void SetStreamId(string? streamId)
    {
        StreamId = streamId;
        Changed?.Invoke();
    }

    public void ResetStream()
    {
        StreamId = null;
        SentCount = 0;
        lock (_lock) _streamRecv.Clear();
        Changed?.Invoke();
    }

    public void IncrementSent()
    {
        SentCount++;
        Changed?.Invoke();
    }

    public void AddStreamRecv(string json)
    {
        lock (_lock)
        {
            if (_streamRecv.Count >= MaxStreamRecv) _streamRecv.RemoveAt(0);
            _streamRecv.Add(json);
        }
        Changed?.Invoke();
    }

    public void SetServices(List<ServiceMetadata> services)
    {
        Services = services;
        Changed?.Invoke();
    }

    public void SetSelected(string? serviceName, string? methodName)
    {
        SelectedServiceName = serviceName;
        SelectedMethodName = methodName;
        Changed?.Invoke();
    }

    // ── 1-탭 정책 ──────────────────────────────────────────────────────────
    // 새 탭이 진입하면 이전 활성 클라이언트는 Evicted 통지를 받고 오버레이 표시.
    public Guid ClaimActive()
    {
        Guid newId;
        bool hadPrevious;
        lock (_lock)
        {
            hadPrevious = _activeClient != null;
            newId = Guid.NewGuid();
            _activeClient = newId;
        }
        if (hadPrevious) Evicted?.Invoke();
        return newId;
    }

    public void Release(Guid clientId)
    {
        lock (_lock)
        {
            if (_activeClient == clientId) _activeClient = null;
        }
    }

    public bool IsActive(Guid clientId)
    {
        lock (_lock) return _activeClient == clientId;
    }
}
