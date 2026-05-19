namespace GrpcWorkbench.Models.Ui;

public sealed record FrameVm(int Index, string Data);

public sealed class IncomingCallVm(string callId, string service, string method, string type)
{
    public string CallId { get; } = callId;
    public string Service { get; } = service;
    public string Method { get; } = method;
    public string Type { get; } = type;
    // BufferMode OFF: Frames에는 최신 1건만 보관. ON: 최근 N건(상한) 보관.
    // 어느 모드든 마지막 항목이 LatestFrame.
    public List<FrameVm> Frames { get; } = [];
    public string? Result { get; set; }
    public bool Expanded { get; set; }
    public bool Pretty { get; set; } = true;
}

/// <summary>
/// Service.Method 단위 수신 집계. 대규모(50+ RPC × 10-100Hz) 부하에서
/// 콜/프레임을 모두 보관하는 대신 RPC별 카운트·rate를 집계하고,
/// BufferMode일 때만 프레임 히스토리를 유지한다.
/// </summary>
public sealed class RpcAggregate(string service, string method, string type)
{
    public string Service { get; } = service;
    public string Method { get; } = method;
    public string Type { get; } = type;
    public string Key { get; } = $"{service}.{method}";

    public int ActiveCalls { get; set; }
    public int TotalCalls { get; set; }
    public long TotalFrames { get; set; }
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public List<IncomingCallVm> RecentCalls { get; } = [];

    // OFF: 콜당 최신 프레임 1건만 (메모리 안전). ON: 콜당 최대 N건 히스토리.
    public bool BufferMode { get; set; }

    public bool Expanded { get; set; }

    // ── 단순 1초 윈도 rate (frames/sec) ─────────────────────────────────────
    private readonly object _rateLock = new();
    private long _windowCount;
    private DateTime _windowStart = DateTime.UtcNow;
    private double _lastRate;

    public void RecordFrame(DateTime now)
    {
        lock (_rateLock)
        {
            var elapsed = (now - _windowStart).TotalSeconds;
            if (elapsed >= 1.0)
            {
                _lastRate = _windowCount / elapsed;
                _windowCount = 0;
                _windowStart = now;
            }
            _windowCount++;
        }
    }

    public double RatePerSec
    {
        get
        {
            lock (_rateLock)
            {
                // 2초 이상 무활동이면 0으로 표시 (잔류 rate 제거)
                if ((DateTime.UtcNow - _windowStart).TotalSeconds > 2.0) return 0;
                return _lastRate;
            }
        }
    }
}

public enum LogLevel { Info, Success, Warn, Error, Send }

public sealed record LogEntry(DateTime Time, string Text, LogLevel Level);
