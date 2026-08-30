using System.Collections.Concurrent;
using System.Diagnostics;
using ASAP.Dds;
using ASAP.Models.Dds;
using Rti.Dds.Subscription;
using Rti.Types.Dynamic;

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
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DdsSampleEntry>> _inbound = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DdsOutboundEntry>> _outbound = new();
    private readonly ConcurrentDictionary<string, long> _publishCount = new();
    private readonly ConcurrentDictionary<string, long> _receiveSeq = new();
    private readonly ConcurrentDictionary<string, (DataReader<DynamicData> Reader, DataAvailableEventHandler Handler)> _readerHandlers = new();
    private readonly Timer _receiveNotificationTimer;
    private int _receiveNotificationPending;

    public event Action? StateChanged;
    public event Action<DdsSubscriptionInfo, DdsSampleEntry>? SampleReceived;

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
        var reader = host.GetOrCreateReader(topicName, typeName, fullQos);

        var info = new DdsSubscriptionInfo
        {
            SubscriptionId = System.Guid.NewGuid().ToString(),
            SessionId = sessionId,
            TopicName = topicName,
            TypeName = typeName,
            StartedAt = DateTime.UtcNow,
        };
        _subscriptions[info.SubscriptionId] = info;

        // DataAvailable 이벤트 구독 — 같은 reader에 여러 subscription이 붙으면 모두에게 broadcast
        DataAvailableEventHandler handler = anyReader => HandleDataAvailable(anyReader, info);
        reader.DataAvailable += handler;
        _readerHandlers[info.SubscriptionId] = (reader, handler);

        _logger.LogInformation("DDS 구독 시작: {Topic} ({Sub})", topicName, info.SubscriptionId);
        RaiseStateChanged();
        return info;
    }

    public void StopSubscription(string subscriptionId)
    {
        if (_subscriptions.TryRemove(subscriptionId, out var info))
        {
            info.IsActive = false;
            if (_readerHandlers.TryRemove(subscriptionId, out var registration))
                registration.Reader.DataAvailable -= registration.Handler;

            // 같은 topic을 다른 sub가 더 보고 있지 않으면 reader 제거
            var stillUsed = _subscriptions.Values.Any(s =>
                s.SessionId == info.SessionId && s.TopicName == info.TopicName);
            if (!stillUsed)
            {
                _sessions.GetHost(info.SessionId)?.RemoveReader(info.TopicName);
            }
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
        if (jsonPayload.Length > MaxPublishPayloadChars)
            return new DdsPublishResult(false, "DDS 메시지 payload는 1MB를 초과할 수 없습니다.");
        var host = _sessions.GetHost(sessionId)
            ?? throw new InvalidOperationException($"DDS 세션 없음: {sessionId}");

        var fullQos = QualifyProfile(sessionId, qosProfileName);
        var writer = host.GetOrCreateWriter(topicName, typeName, fullQos);

        using var sample = host.CreateSample(typeName);
        var writeStarted = 0L;
        try
        {
            DdsJsonConverter.ApplyJson(sample, jsonPayload);
            writeStarted = Stopwatch.GetTimestamp();
            writer.Write(sample);
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
        if (!_outbound.TryGetValue(sessionId, out var q)) return [];
        return q.Reverse().Take(max).ToList();
    }

    public IReadOnlyList<DdsSampleEntry> SnapshotInbound(string sessionId, int max = 50)
    {
        if (!_inbound.TryGetValue(sessionId, out var q)) return [];
        return q.Reverse().Take(max).ToList();
    }

    public long GetOutboundCount(string sessionId) => _publishCount.GetValueOrDefault(sessionId);
    public long GetInboundCount(string sessionId) => _receiveSeq.GetValueOrDefault(sessionId);

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

    // ── DataAvailable handler ─────────────────────────────────────

    private void HandleDataAvailable(AnyDataReader anyReader, DdsSubscriptionInfo info)
    {
        if (!info.IsActive) return;
        try
        {
            var typed = (DataReader<DynamicData>)anyReader;
            using var samples = typed.Take();
            foreach (var s in samples)
            {
                if (!s.Info.ValidData) continue;
                var json = DdsJsonConverter.ToJson(s.Data!);
                var receivedAt = DateTime.UtcNow;
                var sourceTimestampNs = s.Info.SourceTimestamp.Seconds * 1_000_000_000L
                                        + s.Info.SourceTimestamp.Nanoseconds;
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
                info.LastSample = entry;
                EnqueueBounded(
                    _inbound.GetOrAdd(info.SessionId, _ => new ConcurrentQueue<DdsSampleEntry>()),
                    entry,
                    MaxInboundLogPerSession);
                _receiveSeq.AddOrUpdate(info.SessionId, 1, (_, count) => count + 1);
                SampleReceived?.Invoke(info, entry);
            }
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
        var q = _outbound.GetOrAdd(sessionId, _ => new ConcurrentQueue<DdsOutboundEntry>());
        EnqueueBounded(q, entry, MaxOutboundLogPerSession);
        _publishCount.AddOrUpdate(sessionId, 1, (_, count) => count + 1);
    }

    private static void EnqueueBounded<T>(ConcurrentQueue<T> q, T item, int max)
    {
        q.Enqueue(item);
        while (q.Count > max) q.TryDequeue(out _);
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
        if (Interlocked.CompareExchange(ref _receiveNotificationPending, 1, 0) == 0)
            _receiveNotificationTimer.Change(100, Timeout.Infinite);
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
        foreach (var subscriptionId in _subscriptions.Values
                     .Where(item => item.SessionId == sessionId)
                     .Select(item => item.SubscriptionId).ToList())
            StopSubscription(subscriptionId);
        _outbound.TryRemove(sessionId, out _);
        _inbound.TryRemove(sessionId, out _);
        _publishCount.TryRemove(sessionId, out _);
        _receiveSeq.TryRemove(sessionId, out _);
    }

    public void Dispose()
    {
        _sessions.SessionDeleting -= RemoveSession;
        foreach (var subscriptionId in _subscriptions.Keys.ToList())
            StopSubscription(subscriptionId);
        _receiveNotificationTimer.Dispose();
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
