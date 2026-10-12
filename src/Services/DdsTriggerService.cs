using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private readonly IDdsSessionService _sessions;

    private readonly ConcurrentDictionary<string, DdsTrigger> _triggers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _periodicCts = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastOnIncomingFireAt = new();
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _onIncomingFireWindow = new();
    private readonly object _periodicGate = new();
    private readonly object _incomingGate = new();
    private readonly HashSet<Task> _periodicTasks = new();
    private readonly Dictionary<string, object> _versions = new();
    private readonly Dictionary<string, Channel<(DdsTrigger Trigger, object Version)>> _incoming = new();
    private readonly HashSet<Task> _incomingTasks = new();
    private readonly int _incomingCapacity;
    private readonly Func<DdsTrigger, DdsPublishResult> _publish;
    private bool _disposed;

    public event Action? Changed;

    public DdsTriggerService(DdsStateService dds, IDdsSessionService sessions, ILogger<DdsTriggerService> logger)
        : this(dds, sessions, logger, t => dds.Publish(t.SessionId, t.TopicName, t.TypeName, t.QosProfileName, t.JsonPayload), 256) { }

    internal DdsTriggerService(DdsStateService dds, IDdsSessionService sessions, ILogger<DdsTriggerService> logger,
        Func<DdsTrigger, DdsPublishResult> publish, int incomingCapacity)
    {
        _dds = dds;
        _logger = logger;
        _sessions = sessions;
        _publish = publish;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(incomingCapacity);
        _incomingCapacity = incomingCapacity;
        _dds.SampleReceived += OnSampleReceived;
        _sessions.SessionDeleting += RemoveSession;
    }

    public IReadOnlyList<DdsTrigger> Snapshot(string? sessionId = null) =>
        _triggers.Values
            .Where(t => sessionId == null || t.SessionId == sessionId)
            .OrderBy(t => t.Name)
            .ToList();

    public DdsTrigger Add(DdsTrigger trigger)
    {
        lock (_periodicGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopPeriodic(trigger.Id);
            _triggers[trigger.Id] = trigger;
            _versions[trigger.Id] = new object();
            if (trigger.Enabled && trigger.Type == DdsTriggerType.Periodic)
                StartPeriodic(trigger);
        }
        RaiseChanged();
        return trigger;
    }

    public void Update(DdsTrigger trigger)
    {
        lock (_periodicGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _triggers[trigger.Id] = trigger;
            _versions[trigger.Id] = new object();
            StopPeriodic(trigger.Id);
            if (trigger.Enabled && trigger.Type == DdsTriggerType.Periodic)
                StartPeriodic(trigger);
        }
        RaiseChanged();
    }

    public void SetEnabled(string triggerId, bool enabled)
    {
        lock (_periodicGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_triggers.TryGetValue(triggerId, out var t)) return;
            t.Enabled = enabled;
            _versions[triggerId] = new object();
            StopPeriodic(triggerId);
            if (enabled && t.Type == DdsTriggerType.Periodic)
                StartPeriodic(t);
        }
        RaiseChanged();
    }

    public void Remove(string triggerId)
    {
        lock (_periodicGate)
        {
            StopPeriodic(triggerId);
            _triggers.TryRemove(triggerId, out _);
            _versions.Remove(triggerId);
            _lastOnIncomingFireAt.TryRemove(triggerId, out _);
            _onIncomingFireWindow.TryRemove(triggerId, out _);
        }
        RaiseChanged();
    }

    public void ReplaceSessionTriggers(string sessionId, IReadOnlyList<DdsTrigger> triggers)
    {
        lock (_periodicGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var existingIds = _triggers.Values
                .Where(t => t.SessionId == sessionId)
                .Select(t => t.Id)
                .ToList();

            foreach (var triggerId in existingIds)
            {
                StopPeriodic(triggerId);
                _triggers.TryRemove(triggerId, out _);
                _versions.Remove(triggerId);
                _lastOnIncomingFireAt.TryRemove(triggerId, out _);
                _onIncomingFireWindow.TryRemove(triggerId, out _);
            }

            foreach (var trigger in triggers)
            {
                _triggers[trigger.Id] = trigger;
                _versions[trigger.Id] = new object();
                if (trigger.Enabled && trigger.Type == DdsTriggerType.Periodic)
                    StartPeriodic(trigger);
            }

        }
        RaiseChanged();
    }

    public DdsPublishResult FireOnce(string triggerId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (!_triggers.TryGetValue(triggerId, out var t))
            return new DdsPublishResult(false, "트리거 없음");
        return Publish(t);
    }

    public void FireBulk(string triggerId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (!_triggers.TryGetValue(triggerId, out var t)) return;
        var work = () =>
        {
            for (int i = 0; i < t.BulkCount; i++) Publish(t);
        };
        if (t.BulkParallel)
            Parallel.For(0, t.BulkCount, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
            }, _ => Publish(t));
        else
            work();
        RaiseChanged();
    }

    // ── Periodic loop ─────────────────────────────────────────────

    private void StartPeriodic(DdsTrigger trigger)
    {
        lock (_periodicGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopPeriodic(trigger.Id);
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            _periodicCts[trigger.Id] = cts;
            var task = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (trigger.MaxFires.HasValue && trigger.TotalFires >= trigger.MaxFires.Value)
                            break;
                        Publish(trigger, periodicToken: token);
                        RaiseChanged();
                        try { await Task.Delay(Math.Max(1, trigger.IntervalMs), token); }
                        catch (OperationCanceledException) { break; }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DDS 주기 트리거 실패: {Id}", trigger.Id);
                }
                finally
                {
                    lock (_periodicGate)
                    {
                        RemoveIfCurrent(_periodicCts, trigger.Id, cts);
                        cts.Dispose();
                    }
                }
            });
            _periodicTasks.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_periodicGate) _periodicTasks.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal static bool RemoveIfCurrent(ConcurrentDictionary<string, CancellationTokenSource> jobs,
        string id, CancellationTokenSource expected) =>
        ((ICollection<KeyValuePair<string, CancellationTokenSource>>)jobs).Remove(new(id, expected));

    private void StopPeriodic(string triggerId)
    {
        lock (_periodicGate)
        {
            if (_periodicCts.TryRemove(triggerId, out var cts))
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
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

            bool overflow;
            lock (_periodicGate)
            {
                if (_disposed || !t.Enabled || !_triggers.TryGetValue(t.Id, out var current) ||
                    !ReferenceEquals(t, current) || _sessions.Get(t.SessionId) is null) continue;
                if (!TryReserveIncoming(t, now)) continue;
                overflow = !GetIncomingQueue(t.SessionId).Writer.TryWrite((t, _versions[t.Id]));
                if (overflow)
                {
                    Interlocked.Increment(ref t.Errors);
                    t.LastError = "DDS automatic response queue is full; response was not queued.";
                }
            }
            // No DDS call or synchronous Changed observer runs on the receive thread.
            // Queue overflow is visible through Errors/LastError in the state snapshot.
            if (overflow) _logger.LogWarning("DDS automatic response queue full: {Id}", t.Id);
        }
    }

    // Called under _periodicGate. Each session owns an independent bounded worker.
    private Channel<(DdsTrigger Trigger, object Version)> GetIncomingQueue(string sessionId)
    {
        if (_incoming.TryGetValue(sessionId, out var existing)) return existing;
        var queue = Channel.CreateBounded<(DdsTrigger Trigger, object Version)>(new BoundedChannelOptions(_incomingCapacity)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _incoming.Add(sessionId, queue);
        var task = Task.Run(() => ProcessIncomingAsync(queue));
        _incomingTasks.Add(task);
        _ = task.ContinueWith(done =>
        {
            lock (_periodicGate) _incomingTasks.Remove(done);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return queue;
    }

    private async Task ProcessIncomingAsync(Channel<(DdsTrigger Trigger, object Version)> queue)
    {
        await foreach (var work in queue.Reader.ReadAllAsync())
        {
            Publish(work.Trigger, work.Version);
            RaiseChanged();
        }
    }

    // ── Publish helper ────────────────────────────────────────────

    // Reserve before publishing; no blocking DDS operation runs under this lock.
    internal bool TryReserveIncoming(DdsTrigger trigger, DateTime now)
    {
        lock (_incomingGate)
        {
            if (Volatile.Read(ref _disposed)) return false;
            if (trigger.MinFireIntervalMs > 0 &&
                _lastOnIncomingFireAt.TryGetValue(trigger.Id, out var last) &&
                (now - last).TotalMilliseconds < trigger.MinFireIntervalMs) return false;
            if (trigger.MaxFiresPerMinute > 0)
            {
                var window = _onIncomingFireWindow.GetOrAdd(trigger.Id, _ => new Queue<DateTime>());
                while (window.Count > 0 && (now - window.Peek()).TotalSeconds >= 60) window.Dequeue();
                if (window.Count >= trigger.MaxFiresPerMinute) return false;
                window.Enqueue(now);
            }
            _lastOnIncomingFireAt[trigger.Id] = now;
            return true;
        }
    }

    internal DdsPublishResult Publish(DdsTrigger t, object? incomingVersion = null, CancellationToken periodicToken = default)
    {
        // Admission is atomic with shutdown/replacement; never hold this gate across DDS.
        // Attempts include returned failures and thrown exceptions, not just successful writes.
        lock (_periodicGate)
        {
            if (periodicToken.IsCancellationRequested || (periodicToken.CanBeCanceled && !t.Enabled))
                return new DdsPublishResult(false, "주기 발행이 취소되었습니다.");
            if (_disposed || !_triggers.TryGetValue(t.Id, out var current) || !ReferenceEquals(t, current))
                return new DdsPublishResult(false, "트리거가 종료되었거나 교체되었습니다.");
            if (incomingVersion is not null && (!t.Enabled || !_versions.TryGetValue(t.Id, out var version) ||
                !ReferenceEquals(version, incomingVersion) || _sessions.Get(t.SessionId) is null))
                return new DdsPublishResult(false, "예약된 자동응답이 취소되었습니다.");
            if (t.Type == DdsTriggerType.Periodic && t.MaxFires.HasValue && t.TotalFires >= t.MaxFires.Value)
                return new DdsPublishResult(false, "최대 발행 시도 횟수에 도달했습니다.");
            Interlocked.Increment(ref t.TotalFires);
            t.LastFiredAt = DateTime.UtcNow;
        }
        try
        {
            var result = _publish(t);
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
        Task[] pending;
        lock (_periodicGate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var id in _periodicCts.Keys.ToList()) StopPeriodic(id);
            _triggers.Clear();
            _versions.Clear();
            foreach (var queue in _incoming.Values) queue.Writer.TryComplete();
            _incoming.Clear();
            pending = _periodicTasks.Concat(_incomingTasks).ToArray();
        }
        _dds.SampleReceived -= OnSampleReceived;
        _sessions.SessionDeleting -= RemoveSession;
        lock (_incomingGate)
        {
            _lastOnIncomingFireAt.Clear();
            _onIncomingFireWindow.Clear();
        }
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { _logger.LogWarning("DDS jobs are still completing an in-flight publish after shutdown timeout."); }
    }

    private void RaiseChanged()
    {
        var observers = Changed;
        if (observers is null) return;
        foreach (Action observer in observers.GetInvocationList())
            try { observer(); }
            catch (Exception ex) { _logger.LogWarning(ex, "DDS trigger observer failed"); }
    }

    private void RemoveSession(string sessionId)
    {
        lock (_periodicGate)
            if (_incoming.Remove(sessionId, out var queue)) queue.Writer.TryComplete();
        foreach (var triggerId in _triggers.Values.Where(item => item.SessionId == sessionId).Select(item => item.Id).ToList())
            Remove(triggerId);
    }
}
