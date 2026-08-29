using System.Collections.Concurrent;
using ASAP.Models.Dds;

namespace ASAP.Services;

/// <summary>
/// DDS 전용 트리거 — 주기 발행, 다발 발행, 토픽 수신 시 자동 발행.
/// DDS 세션의 주기/다발/수신 반응 발행을 관리한다.
/// </summary>
public sealed class DdsTriggerService : IAsyncDisposable
{
    private readonly DdsStateService _dds;
    private readonly ILogger<DdsTriggerService> _logger;

    private readonly ConcurrentDictionary<string, DdsTrigger> _triggers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _periodicCts = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastOnIncomingFireAt = new();
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _onIncomingFireWindow = new();

    public event Action? Changed;

    public DdsTriggerService(DdsStateService dds, ILogger<DdsTriggerService> logger)
    {
        _dds = dds;
        _logger = logger;
        _dds.SampleReceived += OnSampleReceived;
    }

    public IReadOnlyList<DdsTrigger> Snapshot(string? sessionId = null) =>
        _triggers.Values
            .Where(t => sessionId == null || t.SessionId == sessionId)
            .OrderBy(t => t.Name)
            .ToList();

    public DdsTrigger Add(DdsTrigger trigger)
    {
        _triggers[trigger.Id] = trigger;
        if (trigger.Enabled && trigger.Type == DdsTriggerType.Periodic)
            StartPeriodic(trigger);
        Changed?.Invoke();
        return trigger;
    }

    public void Update(DdsTrigger trigger)
    {
        _triggers[trigger.Id] = trigger;
        StopPeriodic(trigger.Id);
        if (trigger.Enabled && trigger.Type == DdsTriggerType.Periodic)
            StartPeriodic(trigger);
        Changed?.Invoke();
    }

    public void SetEnabled(string triggerId, bool enabled)
    {
        if (!_triggers.TryGetValue(triggerId, out var t)) return;
        t.Enabled = enabled;
        StopPeriodic(triggerId);
        if (enabled && t.Type == DdsTriggerType.Periodic)
            StartPeriodic(t);
        Changed?.Invoke();
    }

    public void Remove(string triggerId)
    {
        StopPeriodic(triggerId);
        _triggers.TryRemove(triggerId, out _);
        Changed?.Invoke();
    }

    public void ReplaceSessionTriggers(string sessionId, IReadOnlyList<DdsTrigger> triggers)
    {
        var existingIds = _triggers.Values
            .Where(t => t.SessionId == sessionId)
            .Select(t => t.Id)
            .ToList();

        foreach (var triggerId in existingIds)
        {
            StopPeriodic(triggerId);
            _triggers.TryRemove(triggerId, out _);
            _lastOnIncomingFireAt.TryRemove(triggerId, out _);
            _onIncomingFireWindow.TryRemove(triggerId, out _);
        }

        foreach (var trigger in triggers)
        {
            _triggers[trigger.Id] = trigger;
            if (trigger.Enabled && trigger.Type == DdsTriggerType.Periodic)
                StartPeriodic(trigger);
        }

        Changed?.Invoke();
    }

    public DdsPublishResult FireOnce(string triggerId)
    {
        if (!_triggers.TryGetValue(triggerId, out var t))
            return new DdsPublishResult(false, "트리거 없음");
        return Publish(t);
    }

    public void FireBulk(string triggerId)
    {
        if (!_triggers.TryGetValue(triggerId, out var t)) return;
        var work = () =>
        {
            for (int i = 0; i < t.BulkCount; i++) Publish(t);
        };
        if (t.BulkParallel)
            Parallel.For(0, t.BulkCount, _ => Publish(t));
        else
            work();
        Changed?.Invoke();
    }

    // ── Periodic loop ─────────────────────────────────────────────

    private void StartPeriodic(DdsTrigger trigger)
    {
        var cts = new CancellationTokenSource();
        _periodicCts[trigger.Id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (trigger.MaxFires.HasValue && trigger.TotalFires >= trigger.MaxFires.Value)
                        break;
                    Publish(trigger);
                    Changed?.Invoke();
                    try { await Task.Delay(Math.Max(1, trigger.IntervalMs), cts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DDS 주기 트리거 실패: {Id}", trigger.Id);
            }
        }, cts.Token);
    }

    private void StopPeriodic(string triggerId)
    {
        if (_periodicCts.TryRemove(triggerId, out var cts))
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* swallow */ }
        }
    }

    // ── OnIncoming hook ───────────────────────────────────────────

    private void OnSampleReceived(DdsSubscriptionInfo info, DdsSampleEntry sample)
    {
        var now = DateTime.UtcNow;
        foreach (var t in _triggers.Values)
        {
            if (!t.Enabled || t.Type != DdsTriggerType.OnIncoming) continue;
            if (t.SessionId != info.SessionId) continue;
            if (!string.IsNullOrEmpty(t.MatchTopicName) &&
                !string.Equals(t.MatchTopicName, info.TopicName, StringComparison.Ordinal))
                continue;

            if (t.BlockSelfTopicLoop && string.Equals(t.TopicName, info.TopicName, StringComparison.Ordinal))
                continue;

            if (t.MinFireIntervalMs > 0 &&
                _lastOnIncomingFireAt.TryGetValue(t.Id, out var lastAt) &&
                (now - lastAt).TotalMilliseconds < t.MinFireIntervalMs)
                continue;

            if (t.MaxFiresPerMinute > 0)
            {
                var q = _onIncomingFireWindow.GetOrAdd(t.Id, _ => new Queue<DateTime>());
                lock (q)
                {
                    while (q.Count > 0 && (now - q.Peek()).TotalSeconds > 60)
                        q.Dequeue();
                    if (q.Count >= t.MaxFiresPerMinute)
                        continue;
                }
            }

            Publish(t);
            _lastOnIncomingFireAt[t.Id] = now;
            if (t.MaxFiresPerMinute > 0)
            {
                var q = _onIncomingFireWindow.GetOrAdd(t.Id, _ => new Queue<DateTime>());
                lock (q)
                {
                    q.Enqueue(now);
                    while (q.Count > 0 && (now - q.Peek()).TotalSeconds > 60)
                        q.Dequeue();
                }
            }
            Changed?.Invoke();
        }
    }

    // ── Publish helper ────────────────────────────────────────────

    private DdsPublishResult Publish(DdsTrigger t)
    {
        try
        {
            var result = _dds.Publish(t.SessionId, t.TopicName, t.TypeName, t.QosProfileName, t.JsonPayload);
            Interlocked.Increment(ref t.TotalFires);
            t.LastFiredAt = DateTime.UtcNow;
            if (!result.Success)
            {
                Interlocked.Increment(ref t.Errors);
                t.LastError = result.Error;
            }
            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref t.Errors);
            t.LastError = ex.Message;
            return new DdsPublishResult(false, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _dds.SampleReceived -= OnSampleReceived;
        foreach (var id in _periodicCts.Keys.ToList())
            StopPeriodic(id);
        _lastOnIncomingFireAt.Clear();
        _onIncomingFireWindow.Clear();
        await Task.CompletedTask;
    }
}
