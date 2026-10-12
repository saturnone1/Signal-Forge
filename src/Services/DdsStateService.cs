using System.Collections.Concurrent;
using System.Diagnostics;
using ASAP.Dds;
using ASAP.Models.Dds;
using ASAP.Models.Session;
using DdsAmbassador.DDSClient;

namespace ASAP.Services;

/// <summary>
/// DDS 세션 운영 상태 — 활성 구독, 최근 샘플, 발행 이력을 관리.
/// UI는 StateChanged 이벤트를 구독하고 Snapshot으로 표시한다.
/// </summary>
public sealed class DdsStateService : IDisposable
{
    private const int MaxInboundLogPerSession = 50;
    private const int MaxOutboundLogPerSession = 50;
    private const int MaxRetainedPayloadChars = 64 * 1024;
    private const int MaxPublishPayloadChars = 1024 * 1024;

    private readonly IDdsSessionService _sessions;
    private readonly ILogger<DdsStateService> _logger;

    private readonly ConcurrentDictionary<string, DdsSubscriptionInfo> _subscriptions = new();
    private readonly ConcurrentDictionary<string, SessionState> _sessionStates = new();
    private sealed class SessionState(DdsSession session)
    {
        public readonly object Gate = new();
        public readonly DdsSession Session = session;
        public readonly Queue<DdsSampleEntry> Inbound = new(MaxInboundLogPerSession);
        public readonly Queue<DdsOutboundEntry> Outbound = new(MaxOutboundLogPerSession);
        public long Published;
        public long Received;
        public bool Closed;
    }
    private readonly ConcurrentDictionary<string, IDisposable> _readerHandlers = new();
    private readonly object _subscriptionGate = new();
    private volatile bool _disposed;
    private readonly Timer _receiveNotificationTimer;
    private int _receiveNotificationPending;

    public event Action? StateChanged;
    private Action<DdsSubscriptionInfo, DdsSampleEntry>? _sampleReceived;
    private Action<DdsSubscriptionInfo, DdsSampleEntry>[] _sampleObservers = [];
    public event Action<DdsSubscriptionInfo, DdsSampleEntry>? SampleReceived
    {
        add { lock (_subscriptionGate) { _sampleReceived += value; CacheSampleObservers(); } }
        remove { lock (_subscriptionGate) { _sampleReceived -= value; CacheSampleObservers(); } }
    }
    // 가입·해제 때만 호출 목록을 만든다. 수신 메시지마다 GetInvocationList 배열을 할당하지 않는다.
    private void CacheSampleObservers() => Volatile.Write(ref _sampleObservers,
        _sampleReceived?.GetInvocationList().Cast<Action<DdsSubscriptionInfo, DdsSampleEntry>>().ToArray() ?? []);

    public DdsStateService(IDdsSessionService sessions, ILogger<DdsStateService> logger)
    {
        _sessions = sessions;
        _logger = logger;
        _sessions.SessionDeleting += RemoveSession;
        _receiveNotificationTimer = new Timer(_ =>
        {
            Interlocked.Exchange(ref _receiveNotificationPending, 0);
            RaiseStateChanged();
        });
    }

    // ── Subscriptions ─────────────────────────────────────────────

    public DdsSubscriptionInfo StartSubscription(
        string sessionId, string topicName, string typeName, string qosProfileName)
    {
        var host = _sessions.GetHost(sessionId)
            ?? throw new InvalidOperationException($"DDS 세션 없음: {sessionId}");

        var fullQos = QualifyProfile(sessionId, qosProfileName);
        return StartSubscription(sessionId, topicName, typeName,
            (handler, error) => host.Subscribe(topicName, typeName, fullQos, handler, error), fullQos,
            () => host.ConnectionState == DdsShutdownState.Open);
    }

    internal DdsSubscriptionInfo StartSubscription(string sessionId, string topicName, string typeName,
        Func<Action<DdsReceivedSample>, IDisposable> subscribe)
        => StartSubscription(sessionId, topicName, typeName, (handler, _) => subscribe(handler));

    internal DdsSubscriptionInfo StartSubscription(string sessionId, string topicName, string typeName,
        Func<Action<DdsReceivedSample>, Action<DdsSubscriptionError>, IDisposable> subscribe, string qosProfile = "", Func<bool>? connectionOpen = null)
    {
        var info = new DdsSubscriptionInfo
        {
            SubscriptionId = System.Guid.NewGuid().ToString(),
            SessionId = sessionId,
            TopicName = topicName,
            TypeName = typeName,
            StartedAt = DateTime.UtcNow,
            QosProfileName = qosProfile,
        };
        var registration = new DeferredSubscription();
        SessionState sessionState;
        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Session IDs are unique. Consult the live registry instead of retaining
            // every deleted ID forever. Deletion observers run outside its lock.
            var session = _sessions.Get(sessionId) ?? throw new InvalidOperationException($"DDS session is not active: {sessionId}");
            sessionState = _sessionStates.GetOrAdd(sessionId, _ => new SessionState(session));
            _readerHandlers[info.SubscriptionId] = registration;
            _subscriptions[info.SubscriptionId] = info;
        }

        try
        {
            // Each handle owns an independent DDSClient subscriber; every subscriber receives the burst.
            registration.Set(subscribe(sample => HandleSample(sample, info, sessionState, connectionOpen), error => HandleSubscriptionError(info, error)));
        }
        catch
        {
            StopSubscription(info.SubscriptionId);
            throw;
        }

        _logger.LogInformation("DDS 구독 시작: {Topic} ({Sub})", topicName, info.SubscriptionId);
        RaiseStateChanged();
        return info;
    }

    private void HandleSubscriptionError(DdsSubscriptionInfo info, DdsSubscriptionError error)
    {
        lock (_subscriptionGate)
        {
            if (!_subscriptions.ContainsKey(info.SubscriptionId) || info.State is DdsSubscriptionState.Closing or DdsSubscriptionState.Closed) return;
            info.LastError = error.Exception.Message;
            info.NativeSubscriptionId = error.SubscriptionId;
            if (error.IsFatal) info.State = DdsSubscriptionState.Faulted;
        }
        RaiseStateChanged();
    }

    public DdsSubscriptionInfo RestartSubscription(string subscriptionId)
    {
        DdsSubscriptionInfo info;
        lock (_subscriptionGate)
        {
            if (!_subscriptions.TryGetValue(subscriptionId, out info!) ||
                info.State is DdsSubscriptionState.Closing or DdsSubscriptionState.Closed)
                throw new InvalidOperationException("Subscription no longer exists or is already restarting.");
            info.State = DdsSubscriptionState.Closing;
        }
        StopSubscription(subscriptionId);
        return StartSubscription(info.SessionId, info.TopicName, info.TypeName, info.QosProfileName);
    }

    public void StopSubscription(string subscriptionId)
    {
        DdsSubscriptionInfo? info;
        IDisposable? registration;
        lock (_subscriptionGate)
        {
            if (!_subscriptions.TryRemove(subscriptionId, out info)) return;
            info.IsActive = false;
            _readerHandlers.TryRemove(subscriptionId, out registration);
        }
        // DDS may wait for callbacks here. No service lock may be held during disposal.
        try { registration?.Dispose(); }
        finally
        {
            _logger.LogInformation("DDS 구독 중지: {Topic} ({Sub})", info.TopicName, subscriptionId);
            RaiseStateChanged();
        }
    }

    public IReadOnlyList<DdsSubscriptionInfo> SnapshotSubscriptions(string? sessionId = null)
        => _subscriptions.Values
            .Where(s => sessionId == null || s.SessionId == sessionId)
            .OrderBy(s => s.StartedAt)
            .ToList();

    // ── Publishing ────────────────────────────────────────────────

    public DdsPublishResult Publish(
        string sessionId, string topicName, string typeName,
        string qosProfileName, string jsonPayload)
    {
        lock (_subscriptionGate) ObjectDisposedException.ThrowIf(_disposed, this);
        if (jsonPayload.Length > MaxPublishPayloadChars)
            return new DdsPublishResult(false, "DDS 메시지 payload는 1MB를 초과할 수 없습니다.");
        var host = _sessions.GetHost(sessionId)
            ?? throw new InvalidOperationException($"DDS 세션 없음: {sessionId}");

        var fullQos = QualifyProfile(sessionId, qosProfileName);

        using var sample = host.CreateSample(typeName);
        var writeStarted = 0L;
        try
        {
            DdsJsonConverter.ApplyJson(sample, jsonPayload);
            host.Publish(topicName, typeName, fullQos, sample, () => writeStarted = Stopwatch.GetTimestamp());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DDS publish 실패: {Topic}", topicName);
            RecordOutbound(sessionId, new DdsOutboundEntry
            {
                Timestamp = DateTime.UtcNow,
                TopicName = topicName,
                TypeName = typeName,
                JsonPayload = RetainPayload(jsonPayload),
                Success = false,
                Error = ex.Message,
                WriteLatencyMs = writeStarted == 0 ? null : Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds,
            });
            RequestStateChanged();
            return new DdsPublishResult(false, ex.Message);
        }

        RecordOutbound(sessionId, new DdsOutboundEntry
        {
            Timestamp = DateTime.UtcNow,
            TopicName = topicName,
            TypeName = typeName,
            JsonPayload = RetainPayload(jsonPayload),
            Success = true,
            WriteLatencyMs = Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds,
        });
        RequestStateChanged();
        return new DdsPublishResult(true, null);
    }

    public IReadOnlyList<DdsOutboundEntry> SnapshotOutbound(string sessionId, int max = 50)
    {
        if (!_sessionStates.TryGetValue(sessionId, out var state)) return [];
        lock (state.Gate) return state.Outbound.Reverse().Take(max).ToList();
    }

    public IReadOnlyList<DdsSampleEntry> SnapshotInbound(string sessionId, int max = 50)
    {
        if (!_sessionStates.TryGetValue(sessionId, out var state)) return [];
        lock (state.Gate) return state.Inbound.Reverse().Take(max).ToList();
    }

    public long GetOutboundCount(string sessionId) => _sessionStates.TryGetValue(sessionId, out var state) ? Interlocked.Read(ref state.Published) : 0;
    public long GetInboundCount(string sessionId) => _sessionStates.TryGetValue(sessionId, out var state) ? Interlocked.Read(ref state.Received) : 0;

    public void RecordExternalPublish(
        string sessionId,
        string topicName,
        string typeName,
        string jsonPayload,
        bool success,
        string? error = null)
    {
        RecordOutbound(sessionId, new DdsOutboundEntry
        {
            Timestamp = DateTime.UtcNow,
            TopicName = topicName,
            TypeName = typeName,
            JsonPayload = RetainPayload(jsonPayload),
            Success = success,
            Error = error,
        });
        RequestStateChanged();
    }

    // ── Typed DDSClient receive callback ──────────────────────────

    private void HandleSample(DdsReceivedSample sample, DdsSubscriptionInfo info, SessionState state, Func<bool>? connectionOpen)
    {
        if (!info.IsActive) return;
        try
        {
            var json = sample.Json;
            var receivedAt = sample.ReceivedAtUtc;
            // SDK에서 전달받은 시각만 사용한다. 메타데이터가 없으면 지연값을 만들지 않는다.
            var sourceTimestampNs = sample.SourceTimestampNs ?? 0;
            var entry = new DdsSampleEntry
            {
                SequenceNumber = Interlocked.Increment(ref info.ReceivedCount),
                TopicName = info.TopicName,
                TypeName = info.TypeName,
                ReceivedAt = receivedAt,
                JsonData = RetainPayload(json),
                SourceTimestampNs = sourceTimestampNs,
                ReceiveLatencyMs = CalculateReceiveLatencyMs(receivedAt, sourceTimestampNs),
            };
            lock (state.Gate)
            {
                if (_disposed || state.Closed || state.Session.State != DdsSessionState.Active ||
                    !info.IsActive || !_subscriptions.ContainsKey(info.SubscriptionId) || connectionOpen?.Invoke() == false) return;
                info.LastSample = entry;
                EnqueueBounded(state.Inbound, entry, MaxInboundLogPerSession);
                Interlocked.Increment(ref state.Received);
            }
            var observers = Volatile.Read(ref _sampleObservers);
            foreach (var observer in observers)
                try { observer(info, entry); }
                catch (Exception ex) { _logger.LogWarning(ex, "DDS sample observer failed: {Subscription}", info.SubscriptionId); }
            RequestStateChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DDS 샘플 수신 처리 실패: {Topic}", info.TopicName);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private void RecordOutbound(string sessionId, DdsOutboundEntry entry)
    {
        SessionState? state;
        if (!_sessionStates.TryGetValue(sessionId, out state))
        {
            lock (_subscriptionGate)
            {
                if (_disposed || _sessions.Get(sessionId) is not { } session) return;
                state = _sessionStates.GetOrAdd(sessionId, _ => new SessionState(session));
            }
        }
        lock (state.Gate)
        {
            if (_disposed || state.Closed || _sessions.Get(sessionId) is null) return;
            EnqueueBounded(state.Outbound, entry, MaxOutboundLogPerSession);
            Interlocked.Increment(ref state.Published);
        }
    }

    private static void EnqueueBounded<T>(Queue<T> q, T item, int max)
    {
        // 호출자는 해당 세션 잠금을 보유한다. 고정 크기 이력만 제거하고 수신 이벤트·누적 수는 모두 유지한다.
        if (q.Count == max) q.Dequeue();
        q.Enqueue(item);
    }

    private string QualifyProfile(string sessionId, string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return string.Empty;
        if (profileName.Contains("::")) return profileName;
        var library = _sessions.Get(sessionId)?.QosLibraryName;
        if (string.IsNullOrWhiteSpace(library))
            throw new InvalidOperationException("QoS 라이브러리 이름이 없습니다.");
        return $"{library}::{profileName}";
    }

    private static string RetainPayload(string value)
        => value.Length <= MaxRetainedPayloadChars
            ? value
            : value[..MaxRetainedPayloadChars] + "\n/* retained payload truncated */";

    public static double? CalculateReceiveLatencyMs(DateTime receivedAtUtc, long sourceTimestampNs)
    {
        if (sourceTimestampNs <= 0) return null;
        var utc = receivedAtUtc.ToUniversalTime();
        var receivedNs = new DateTimeOffset(utc).ToUnixTimeMilliseconds() * 1_000_000L
                         + utc.Ticks % TimeSpan.TicksPerMillisecond * 100L;
        return (receivedNs - sourceTimestampNs) / 1_000_000d;
    }

    private void RequestStateChanged()
    {
        if (_disposed || Interlocked.CompareExchange(ref _receiveNotificationPending, 1, 0) != 0) return;
        lock (_subscriptionGate)
        {
            if (_disposed) return;
            _receiveNotificationTimer.Change(100, Timeout.Infinite);
        }
    }

    private void RaiseStateChanged()
    {
        var handlers = StateChanged;
        if (handlers == null) return;

        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex) { _logger.LogDebug(ex, "DDS 상태 UI 알림 처리 실패"); }
        }
    }

    private void RemoveSession(string sessionId)
    {
        string[] subscriptions;
        lock (_subscriptionGate)
        {
            subscriptions = _subscriptions.Values.Where(item => item.SessionId == sessionId)
                .Select(item => item.SubscriptionId).ToArray();
            if (_sessionStates.TryRemove(sessionId, out var state))
                lock (state.Gate) { state.Closed = true; state.Inbound.Clear(); state.Outbound.Clear(); }
        }
        var failures = StopSubscriptions(subscriptions);
        if (failures.Count > 0) throw new AggregateException("DDS session subscription cleanup failed.", failures);
    }

    private List<Exception> StopSubscriptions(IEnumerable<string> ids)
    {
        var failures = new List<Exception>();
        foreach (var id in ids)
            try { StopSubscription(id); }
            catch (Exception ex) { failures.Add(ex); }
        return failures;
    }

    public void Dispose()
    {
        lock (_subscriptionGate)
        {
            if (_disposed) return;
            _disposed = true;
            _receiveNotificationTimer.Dispose();
        }
        _sessions.SessionDeleting -= RemoveSession;
        var failures = StopSubscriptions(_subscriptions.Keys.ToArray());
        foreach (var state in _sessionStates.Values)
            lock (state.Gate) { state.Closed = true; state.Inbound.Clear(); state.Outbound.Clear(); }
        _sessionStates.Clear();
        if (failures.Count > 0) throw new AggregateException("DDS subscription cleanup failed.", failures);
    }
}

public sealed record DdsPublishResult(bool Success, string? Error);

public sealed class DdsOutboundEntry
{
    public required DateTime Timestamp { get; init; }
    public required string TopicName { get; init; }
    public required string TypeName { get; init; }
    public required string JsonPayload { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public double? WriteLatencyMs { get; init; }
}
