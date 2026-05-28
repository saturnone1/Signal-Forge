using System.Collections.Concurrent;
using ASAP.Dds;
using ASAP.Models.Dds;
using Rti.Dds.Subscription;
using Rti.Types.Dynamic;

namespace ASAP.Services;

/// <summary>
/// DDS 세션 운영 상태 — 활성 구독, 최근 샘플, 발행 이력을 관리.
/// UI는 StateChanged 이벤트를 구독하고 Snapshot으로 표시한다.
/// </summary>
public sealed class DdsStateService
{
    private const int MaxSamplesPerSubscription = 100;
    private const int MaxOutboundLogPerSession = 100;

    private readonly IDdsSessionService _sessions;
    private readonly ILogger<DdsStateService> _logger;

    private readonly ConcurrentDictionary<string, DdsSubscriptionInfo> _subscriptions = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DdsSampleEntry>> _samples = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DdsOutboundEntry>> _outbound = new();
    private readonly ConcurrentDictionary<string, long> _publishSeq = new();

    public event Action? StateChanged;
    public event Action<DdsSubscriptionInfo, DdsSampleEntry>? SampleReceived;

    public DdsStateService(IDdsSessionService sessions, ILogger<DdsStateService> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    // ── Subscriptions ─────────────────────────────────────────────

    public DdsSubscriptionInfo StartSubscription(
        string sessionId, string topicName, string typeName, string qosProfileName)
    {
        var host = _sessions.GetHost(sessionId)
            ?? throw new InvalidOperationException($"DDS 세션 없음: {sessionId}");

        var fullQos = QualifyProfile(qosProfileName);
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
        _samples[info.SubscriptionId] = new ConcurrentQueue<DdsSampleEntry>();

        // DataAvailable 이벤트 구독 — 같은 reader에 여러 subscription이 붙으면 모두에게 broadcast
        reader.DataAvailable += anyReader => HandleDataAvailable(anyReader, info);

        _logger.LogInformation("DDS 구독 시작: {Topic} ({Sub})", topicName, info.SubscriptionId);
        StateChanged?.Invoke();
        return info;
    }

    public void StopSubscription(string subscriptionId)
    {
        if (_subscriptions.TryRemove(subscriptionId, out var info))
        {
            info.IsActive = false;
            _samples.TryRemove(subscriptionId, out _);

            // 같은 topic을 다른 sub가 더 보고 있지 않으면 reader 제거
            var stillUsed = _subscriptions.Values.Any(s =>
                s.SessionId == info.SessionId && s.TopicName == info.TopicName);
            if (!stillUsed)
            {
                _sessions.GetHost(info.SessionId)?.RemoveReader(info.TopicName);
            }
            _logger.LogInformation("DDS 구독 중지: {Topic} ({Sub})", info.TopicName, subscriptionId);
            StateChanged?.Invoke();
        }
    }

    public IReadOnlyList<DdsSubscriptionInfo> SnapshotSubscriptions(string? sessionId = null)
        => _subscriptions.Values
            .Where(s => sessionId == null || s.SessionId == sessionId)
            .OrderBy(s => s.StartedAt)
            .ToList();

    public IReadOnlyList<DdsSampleEntry> SnapshotSamples(string subscriptionId, int max = 50)
    {
        if (!_samples.TryGetValue(subscriptionId, out var q)) return [];
        return q.Reverse().Take(max).ToList();
    }

    // ── Publishing ────────────────────────────────────────────────

    public DdsPublishResult Publish(
        string sessionId, string topicName, string typeName,
        string qosProfileName, string jsonPayload)
    {
        var host = _sessions.GetHost(sessionId)
            ?? throw new InvalidOperationException($"DDS 세션 없음: {sessionId}");

        var fullQos = QualifyProfile(qosProfileName);
        var writer = host.GetOrCreateWriter(topicName, typeName, fullQos);

        using var sample = host.CreateSample(typeName);
        try
        {
            DdsJsonConverter.ApplyJson(sample, jsonPayload);
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
                JsonPayload = jsonPayload,
                Success = false,
                Error = ex.Message,
            });
            StateChanged?.Invoke();
            return new DdsPublishResult(false, ex.Message);
        }

        RecordOutbound(sessionId, new DdsOutboundEntry
        {
            Timestamp = DateTime.UtcNow,
            TopicName = topicName,
            TypeName = typeName,
            JsonPayload = jsonPayload,
            Success = true,
        });
        StateChanged?.Invoke();
        return new DdsPublishResult(true, null);
    }

    public IReadOnlyList<DdsOutboundEntry> SnapshotOutbound(string sessionId, int max = 50)
    {
        if (!_outbound.TryGetValue(sessionId, out var q)) return [];
        return q.Reverse().Take(max).ToList();
    }

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
            JsonPayload = jsonPayload,
            Success = success,
            Error = error,
        });
        StateChanged?.Invoke();
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
                var entry = new DdsSampleEntry
                {
                    SequenceNumber = Interlocked.Increment(ref info.ReceivedCount),
                    TopicName = info.TopicName,
                    TypeName = info.TypeName,
                    ReceivedAt = DateTime.UtcNow,
                    JsonData = json,
                    SourceTimestampNs = s.Info.SourceTimestamp.Seconds * 1_000_000_000L
                                        + s.Info.SourceTimestamp.Nanoseconds,
                };
                info.LastSample = entry;
                EnqueueBounded(_samples[info.SubscriptionId], entry, MaxSamplesPerSubscription);
                SampleReceived?.Invoke(info, entry);
            }
            StateChanged?.Invoke();
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
    }

    private static void EnqueueBounded<T>(ConcurrentQueue<T> q, T item, int max)
    {
        q.Enqueue(item);
        while (q.Count > max) q.TryDequeue(out _);
    }

    private static string QualifyProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return string.Empty;
        return profileName.Contains("::") ? profileName : $"AmbassadorProfiles::{profileName}";
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
}
