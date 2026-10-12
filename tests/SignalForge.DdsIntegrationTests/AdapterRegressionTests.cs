using System.Text.Json;
using ASAP.Dds;
using ASAP.Models.Session;
using ASAP.Models.Dds;
using System.Collections.Concurrent;
using System.Reflection;
using ASAP.Services;
using Microsoft.Extensions.Logging.Abstractions;

internal static class AdapterRegressionTests
{
    public static async Task Run(Action<bool, string> check)
    {
        await CheckSessionIsolation(check);
        await CheckLateRecords(check);
        await CheckIncomingQueue(check);
        await CheckObserverAndCleanupFailures(check);
        const string small = """<dds><types><module name="Common"><struct name="Status"><member name="Text" type="string" stringMaxLength="2"/></struct></module></types></dds>""";
        var schemas = new DdsSchemaCatalog(new[] { ("RegressionA", small), ("RegressionB", small.Replace("\"2\"", "\"8\"")) });
        using var a = new DdsTypedSample(new DdsModules_RegressionA.Common.Status());
        using var b = new DdsTypedSample(new DdsModules_RegressionB.Common.Status());
        DdsJsonConverter.ApplyJson(b, """{"Text":"abcdef"}""", schemas);
        check(((DdsModules_RegressionB.Common.Status)b.Value).Text == "abcdef", "same Common::Status wire name uses module B's larger bound");
        try
        {
            DdsJsonConverter.ApplyJson(a, """{"Text":"abcdef"}""", schemas);
            throw new InvalidOperationException("Module A's bound was not enforced.");
        }
        catch (JsonException ex) when (ex.Message.Contains("2 UTF-8"))
        {
            check(true, "same Common::Status wire name retains module A's smaller bound");
        }
        var position = new DdsModules_DDSSim.STRUCT.Position8
        {
            LocationX = double.NaN, LocationY = double.PositiveInfinity, LocationZ = double.NegativeInfinity,
        };
        using var sample = new DdsTypedSample(position);
        var json = DdsJsonConverter.ToJson(sample);
        using (var document = JsonDocument.Parse(json))
        {
            check(document.RootElement.GetProperty("LocationX").GetString() == "NaN" &&
                document.RootElement.GetProperty("LocationY").GetString() == "Infinity" &&
                document.RootElement.GetProperty("LocationZ").GetString() == "-Infinity", "nonfinite DDS floats serialize as valid named JSON literals");
        }
        DdsJsonConverter.ApplyJson(sample, json);
        var roundtrip = (DdsModules_DDSSim.STRUCT.Position8)sample.Value;
        check(double.IsNaN(roundtrip.LocationX) && double.IsPositiveInfinity(roundtrip.LocationY) &&
            double.IsNegativeInfinity(roundtrip.LocationZ), "NaN and both infinities roundtrip without dropping samples");

        var sessions = new FakeSessions();
        using var state = new DdsStateService(sessions, NullLogger<DdsStateService>.Instance);
        Action<DdsAmbassador.DDSClient.DdsSubscriptionError>? failFirst = null;
        var faulty = state.StartSubscription("fault", "Shared", "Telemetry::Heartbeat", (_, error) =>
        {
            failFirst = error;
            return new CountingHandle();
        });
        var healthy = state.StartSubscription("fault", "Shared", "Telemetry::Heartbeat", _ => new CountingHandle());
        failFirst!(new("Shared", new Exception("native reader failed"), true) { SubscriptionId = "native-first" });
        check(faulty.State == DdsSubscriptionState.Faulted && !faulty.IsActive && healthy.IsActive &&
            faulty.NativeSubscriptionId == "native-first" && faulty.LastError == "native reader failed",
            "fatal error faults only its own same-topic subscription and preserves diagnostics");
        state.StopSubscription(faulty.SubscriptionId);
        state.StopSubscription(healthy.SubscriptionId);
        var immediateFailure = state.StartSubscription("fault", "Immediate", "Telemetry::Heartbeat", (_, error) =>
        {
            error(new("Immediate", new Exception("failed before return"), true));
            return new CountingHandle();
        });
        check(immediateFailure.State == DdsSubscriptionState.Faulted,
            "fatal callback before Subscribe returns cannot be overwritten as active");
        state.StopSubscription(immediateFailure.SubscriptionId);
        var handle = new CountingHandle(() =>
        {
            if (!Task.Run(() => state.StopSubscription("unregistered")).Wait(TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException("Service lock was held across handle disposal.");
        });
        state.SampleReceived += (info, _) => state.StopSubscription(info.SubscriptionId);
        var stopped = state.StartSubscription("self-stop", "Shared", "Telemetry::Heartbeat", deliver =>
        {
            deliver(new DdsReceivedSample("{\"Counter\":1}", DateTime.UtcNow, null));
            return handle; // callback stopped the registration before this handle existed
        });
        check(!stopped.IsActive && state.SnapshotSubscriptions().Count == 0 && handle.DisposeCount == 1,
            "immediate callback self-stop disposes late reader exactly once outside service lock");
        state.StopSubscription(stopped.SubscriptionId);
        check(handle.DisposeCount == 1, "repeated stop does not dispose late handle twice");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var lateHandle = new CountingHandle();
        var start = Task.Run(() => state.StartSubscription("closing", "Shared", "Telemetry::Heartbeat", _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Regression test release missing.");
            return lateHandle;
        }));
        if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Subscription did not enter creation.");
        try { sessions.RaiseDeleting("closing"); }
        finally { release.Set(); }
        var closed = await start;
        check(!closed.IsActive && lateHandle.DisposeCount == 1 && state.SnapshotSubscriptions("closing").Count == 0,
            "session deletion during Subscribe disposes returned reader without orphan registration");
        var called = false;
        try
        {
            state.StartSubscription("closing", "Shared", "Telemetry::Heartbeat", _ => { called = true; return new CountingHandle(); });
            throw new Exception("Closed session accepted a new reader.");
        }
        catch (InvalidOperationException)
        {
            check(!called, "closed session rejects subscription before native creation");
        }

        var service = new DdsSessionService(null!, NullLogger<DdsSessionService>.Instance);
        var firstHost = new AsyncHandle();
        var secondHost = new AsyncHandle();
        service.CreateOwnedSession(new DdsSession { SessionId = "first" }, () => firstHost);
        service.CreateOwnedSession(new DdsSession { SessionId = "second" }, () => secondHost);
        var notified = 0;
        service.SessionDeleting += _ => throw new InvalidOperationException("Injected observer failure");
        service.SessionDeleting += _ => Interlocked.Increment(ref notified);
        await service.DisposeAsync();
        check(notified == 2 && service.GetAll().Count == 0 && firstHost.Disposals == 1 && secondHost.Disposals == 1,
            "throwing deletion observer does not prevent other observers or remaining session cleanup");

        var racing = new DdsSessionService(null!, NullLogger<DdsSessionService>.Instance);
        using var creating = new ManualResetEventSlim();
        using var finishCreation = new ManualResetEventSlim();
        var lateHost = new AsyncHandle();
        var creation = Task.Run(() => racing.CreateOwnedSession(new DdsSession { SessionId = "late" }, () =>
        {
            creating.Set();
            if (!finishCreation.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return lateHost;
        }));
        if (!creating.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        var shutdown = racing.DisposeAsync().AsTask();
        check(!shutdown.IsCompleted, "shutdown waits for reserved native creation");
        finishCreation.Set();
        try { await creation; throw new Exception("Closing creation was published"); }
        catch (ObjectDisposedException) { }
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        check(lateHost.Disposals == 1 && racing.GetAll().Count == 0, "late-created host disposed exactly once");
        try { racing.CreateOwnedSession(new DdsSession { SessionId = "after" }, () => throw new Exception("Factory called")); throw new Exception("Creation allowed"); }
        catch (ObjectDisposedException) { check(true, "creation rejected after service shutdown before factory runs"); }

        var deleting = new DdsSessionService(null!, NullLogger<DdsSessionService>.Instance);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowHost = new AsyncHandle(releaseClose.Task);
        var closingSession = deleting.CreateOwnedSession(new DdsSession { SessionId = "shared-close" }, () => slowHost);
        var close1 = deleting.DeleteAsync(closingSession.SessionId);
        var close2 = deleting.DeleteAsync(closingSession.SessionId);
        check(ReferenceEquals(close1, close2) && closingSession.State == DdsSessionState.Closing && deleting.Get(closingSession.SessionId) is null,
            "concurrent deletion shares one completion and hides closing session");
        releaseClose.SetResult();
        await close1;
        check(slowHost.Disposals == 1 && closingSession.State == DdsSessionState.Closed, "session closes once after owned resource completes");

        var bounded = new DdsSessionService(null!, NullLogger<DdsSessionService>.Instance, TimeSpan.FromMilliseconds(30));
        var finishSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retained = new AsyncHandle(finishSlow.Task);
        bounded.CreateOwnedSession(new DdsSession { SessionId = "bounded" }, () => retained);
        try { await bounded.DeleteAsync("bounded"); throw new Exception("Unbounded close returned success"); }
        catch (TimeoutException) { }
        check(bounded.GetAll().Single().State == DdsSessionState.Closing && bounded.GetHost("bounded") is null,
            "timeout leaves closing session visible and unusable while ownership is retained");
        finishSlow.SetResult();
        await bounded.DeleteAsync("bounded");
        check(retained.Disposals == 1 && bounded.GetAll().Count == 0, "late cleanup removes retained session exactly once");
        var boundedShutdown = new DdsSessionService(null!, NullLogger<DdsSessionService>.Instance, TimeSpan.FromMilliseconds(30));
        var finishShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        boundedShutdown.CreateOwnedSession(new DdsSession { SessionId = "shutdown-timeout" }, () => new AsyncHandle(finishShutdown.Task));
        try { await boundedShutdown.DisposeAsync(); throw new Exception("Shutdown should time out"); }
        catch (TimeoutException) { }
        check(boundedShutdown.GetAll().Single().State == DdsSessionState.Closing, "service shutdown is bounded without discarding resource owner");
        finishShutdown.SetResult();
        await boundedShutdown.DeleteAsync("shutdown-timeout");
        var failedService = new DdsSessionService(null!, NullLogger<DdsSessionService>.Instance);
        failedService.CreateOwnedSession(new DdsSession { SessionId = "failed" },
            () => new AsyncHandle(Task.FromException(new InvalidOperationException("cleanup failed"))));
        try { await failedService.DeleteAsync("failed"); throw new Exception("Failure lost"); }
        catch (InvalidOperationException) { }
        check(failedService.GetAll().Single().State == DdsSessionState.Faulted &&
            failedService.GetAll().Single().CleanupError == "cleanup failed", "failed cleanup remains queryable");

        var jobs = new ConcurrentDictionary<string, CancellationTokenSource>();
        using var oldJob = new CancellationTokenSource();
        using var newJob = new CancellationTokenSource();
        jobs["replaced"] = newJob;
        check(!DdsTriggerService.RemoveIfCurrent(jobs, "replaced", oldJob) && jobs["replaced"] == newJob,
            "retired periodic job cannot remove replacement cancellation handle");
        check(DdsTriggerService.RemoveIfCurrent(jobs, "replaced", newJob) && jobs.IsEmpty,
            "periodic job removes only its own cancellation handle");

        await using var triggers = new DdsTriggerService(state, sessions, NullLogger<DdsTriggerService>.Instance);
        var limited = new DdsTrigger { SessionId = "limits", MaxFiresPerMinute = 1 };
        var now = DateTime.UtcNow;
        var accepted = 0;
        Parallel.For(0, 64, _ => { if (triggers.TryReserveIncoming(limited, now)) Interlocked.Increment(ref accepted); });
        check(accepted == 1, "64 concurrent receives reserve exactly one per-minute publish slot");
        check(triggers.TryReserveIncoming(limited, now.AddMinutes(1)), "per-minute reservation expires at window boundary");
        var interval = new DdsTrigger { SessionId = "limits", MinFireIntervalMs = 1000 };
        accepted = 0;
        Parallel.For(0, 64, _ => { if (triggers.TryReserveIncoming(interval, now)) Interlocked.Increment(ref accepted); });
        check(accepted == 1 && triggers.TryReserveIncoming(interval, now.AddSeconds(1)),
            "concurrent receives respect minimum interval and allow next interval");
        triggers.Add(new DdsTrigger { SessionId = "missing", Enabled = true, Type = DdsTriggerType.Periodic });
        await triggers.DisposeAsync();
        check(true, "periodic shutdown cancels and joins tracked jobs");
    }

    private static async Task CheckObserverAndCleanupFailures(Action<bool, string> check)
    {
        var sessions = new FakeSessions();
        var disposal = new DdsStateService(sessions, NullLogger<DdsStateService>.Instance);
        var first = new CountingHandle(() => throw new InvalidOperationException("first cleanup"));
        var second = new CountingHandle(() => throw new InvalidOperationException("second cleanup"));
        disposal.StartSubscription("dispose", "Shared", "Type", _ => first);
        disposal.StartSubscription("dispose", "Shared", "Type", _ => second);
        try { disposal.Dispose(); throw new Exception("Cleanup errors were hidden"); }
        catch (AggregateException ex) { check(ex.InnerExceptions.Count == 2, "all cleanup failures are preserved"); }
        disposal.Dispose();
        check(first.DisposeCount == 1 && second.DisposeCount == 1 && disposal.SnapshotSubscriptions().Count == 0,
            "one failed disposal cannot strand peer subscriptions; repeated disposal is idempotent");
        var timer = (Timer)typeof(DdsStateService).GetField("_receiveNotificationTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(disposal)!;
        var timerClosed = false;
        try { timerClosed = !timer.Change(100, Timeout.Infinite); }
        catch (ObjectDisposedException) { timerClosed = true; }
        check(timerClosed, "notification timer disposed even when every reader cleanup fails");

        using var state = new DdsStateService(sessions, NullLogger<DdsStateService>.Instance);
        var bad = new CountingHandle(() => throw new InvalidOperationException("reader cleanup"));
        var good = new CountingHandle();
        state.StartSubscription("delete", "Shared", "Type", _ => bad);
        state.StartSubscription("delete", "Shared", "Type", _ => good);
        state.RecordExternalPublish("delete", "Shared", "Type", "{}", true);
        try { sessions.RaiseDeleting("delete"); throw new Exception("Delete error hidden"); }
        catch (AggregateException) { }
        check(bad.DisposeCount == 1 && good.DisposeCount == 1 && state.SnapshotSubscriptions("delete").Count == 0 &&
            state.SnapshotOutbound("delete").Count == 0 && state.GetOutboundCount("delete") == 0,
            "session cleanup continues through failing readers and removes retained history");

        Action<DdsReceivedSample>? deliver = null;
        state.StartSubscription("events", "Incoming", "Type", handler => { deliver = handler; return new CountingHandle(); });
        var downstream = 0;
        state.SampleReceived += (_, _) => throw new InvalidOperationException("sample observer");
        state.SampleReceived += (_, _) => downstream++;
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.StateChanged += () => notified.TrySetResult();
        deliver!(new("{}", DateTime.UtcNow, null));
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
        check(downstream == 1 && state.GetInboundCount("events") == 1,
            "throwing sample observer cannot suppress downstream delivery or timer notification");

        await using var triggers = new DdsTriggerService(state, sessions, NullLogger<DdsTriggerService>.Instance);
        var changed = 0;
        triggers.Changed += () => throw new InvalidOperationException("trigger observer");
        triggers.Changed += () => Interlocked.Increment(ref changed);
        var a = new DdsTrigger { SessionId = "events", Type = DdsTriggerType.OnIncoming, Enabled = true, TopicName = "Outgoing" };
        var b = new DdsTrigger { SessionId = "events", Type = DdsTriggerType.OnIncoming, Enabled = true, TopicName = "Outgoing" };
        triggers.Add(a); triggers.Add(b);
        deliver(new("{}", DateTime.UtcNow, null));
        var incomingDeadline = DateTime.UtcNow.AddSeconds(3);
        while (Volatile.Read(ref changed) < 4 && DateTime.UtcNow < incomingDeadline) await Task.Delay(10);
        check(a.Errors == 1 && b.Errors == 1 && changed == 4,
            "throwing Changed observer cannot suppress other incoming triggers or observers");
        sessions.RaiseDeleting("events");
        check(triggers.Snapshot("events").Count == 0 && changed == 6,
            "throwing Changed observer cannot interrupt session trigger removal");

        var periodic = new DdsTrigger { SessionId = "missing", Type = DdsTriggerType.Periodic, Enabled = true, IntervalMs = 1 };
        triggers.Add(periodic);
        var limit = DateTime.UtcNow.AddSeconds(3);
        while (Interlocked.Read(ref periodic.Errors) < 3 && DateTime.UtcNow < limit) await Task.Delay(10);
        triggers.Remove(periodic.Id);
        check(periodic.Errors >= 3, "periodic job survives repeated observer exceptions");

        var bounded = new DdsTrigger { SessionId = "missing", Type = DdsTriggerType.Periodic, Enabled = true, IntervalMs = 1, MaxFires = 1 };
        triggers.Add(bounded);
        limit = DateTime.UtcNow.AddSeconds(3);
        while (Interlocked.Read(ref bounded.Errors) < 1 && DateTime.UtcNow < limit) await Task.Delay(10);
        await Task.Delay(30);
        check(bounded.TotalFires == 1 && bounded.Errors == 1 && bounded.LastFiredAt.HasValue,
            "thrown publish failure consumes exactly one maximum attempt");
        Parallel.For(0, 32, _ => triggers.FireOnce(bounded.Id));
        check(bounded.TotalFires == 1 && bounded.Errors == 1, "concurrent manual calls cannot exceed periodic maximum");
        await triggers.DisposeAsync();
        var errorsBefore = bounded.Errors;
        foreach (var action in new Action[] { () => triggers.FireOnce(bounded.Id), () => triggers.FireBulk(bounded.Id),
            () => triggers.Add(bounded), () => triggers.Update(bounded), () => triggers.SetEnabled(bounded.Id, true),
            () => triggers.ReplaceSessionTriggers("missing", [bounded]) })
        {
            try { action(); throw new Exception("Disposed service accepted work"); }
            catch (ObjectDisposedException) { }
        }
        check(triggers.Snapshot().Count == 0 && bounded.Errors == errorsBefore,
            "disposed trigger service rejects sending and mutation without admitting work");
    }

    private static async Task CheckSessionIsolation(Action<bool, string> check)
    {
        var log = new DdsSessionLog();
        Parallel.For(0, 10000, i =>
        {
            log.Add(new() { Message = i.ToString() });
            foreach (var entry in log) _ = entry.Message;
            if (i % 101 == 0) log.Clear();
        });
        check(log.Count <= 500, "concurrent log writers, clears and snapshot enumeration stay bounded and safe");
        var sessions = new FakeSessions();
        using var state = new DdsStateService(sessions, NullLogger<DdsStateService>.Instance);
        Action<DdsReceivedSample>? slow = null, fast = null;
        state.StartSubscription("slow", "In", "Type", h => { slow = h; return new CountingHandle(); });
        state.StartSubscription("fast", "In", "Type", h => { fast = h; return new CountingHandle(); });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var fastDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        await using var triggers = new DdsTriggerService(state, sessions, NullLogger<DdsTriggerService>.Instance, t =>
        {
            Interlocked.Increment(ref writes);
            if (t.SessionId == "slow") { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
            else fastDone.TrySetResult();
            return new(true, null);
        }, 2);
        foreach (var id in new[] { "slow", "fast" })
            triggers.Add(new() { SessionId = id, Enabled = true, Type = DdsTriggerType.OnIncoming, TopicName = "Out" });
        try
        {
            slow!(new("{}", DateTime.UtcNow, null));
            if (!entered.Wait(2000)) throw new TimeoutException();
            fast!(new("{}", DateTime.UtcNow, null));
            await fastDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(true, "blocked automatic reply in one session does not delay another session");
        }
        finally { release.Set(); }
        var periodic = new DdsTrigger { SessionId = "fast", Type = DdsTriggerType.Periodic, Enabled = false };
        triggers.Add(periodic);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var before = Volatile.Read(ref writes);
        var result = triggers.Publish(periodic, periodicToken: cancelled.Token);
        check(!result.Success && writes == before && periodic.TotalFires == 0,
            "cancelled periodic iteration is rejected at publish admission");
    }

    private static async Task CheckLateRecords(Action<bool, string> check)
    {
        await using var sessions = new DdsSessionService(null!, NullLogger<DdsSessionService>.Instance);
        using var state = new DdsStateService(sessions, NullLogger<DdsStateService>.Instance);
        sessions.CreateOwnedSession(new DdsSession { SessionId = "late-record" }, () => new AsyncHandle());
        Action<DdsReceivedSample>? deliver = null;
        state.StartSubscription("late-record", "Incoming", "Type", callback => { deliver = callback; return new CountingHandle(); });
        state.RecordExternalPublish("late-record", "Outgoing", "Type", "{}", true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = Task.Run(async () =>
        {
            await release.Task;
            state.RecordExternalPublish("late-record", "Outgoing", "Type", "{}", false, "closed during publish");
            deliver!(new("{}", DateTime.UtcNow, null));
        });
        await sessions.DeleteAsync("late-record");
        release.SetResult();
        await late;
        check(state.SnapshotOutbound("late-record").Count == 0 && state.GetOutboundCount("late-record") == 0 &&
            state.SnapshotInbound("late-record").Count == 0 && state.GetInboundCount("late-record") == 0,
            "late publish completion and receive callback cannot resurrect deleted session history");
        for (int i = 0; i < 100; i++)
        {
            var id = "cycle-" + i;
            sessions.CreateOwnedSession(new DdsSession { SessionId = id }, () => new AsyncHandle());
            state.RecordExternalPublish(id, "Outgoing", "Type", "{}", true);
            await sessions.DeleteAsync(id);
        }
        check(sessions.GetAll().Count == 0 && state.SnapshotSubscriptions().Count == 0 &&
            typeof(DdsStateService).GetField("_closedSessions", BindingFlags.Instance | BindingFlags.NonPublic) is null,
            "session churn retains no deleted-ID tombstone collection");
        sessions.CreateOwnedSession(new DdsSession { SessionId = "disposed-state" }, () => new AsyncHandle());
        state.Dispose();
        state.RecordExternalPublish("disposed-state", "Outgoing", "Type", "{}", true);
        check(state.GetOutboundCount("disposed-state") == 0, "late external records ignored after state disposal");
        try { state.Publish("disposed-state", "Outgoing", "Type", "P", "{}"); throw new Exception("Disposed publish admitted"); }
        catch (ObjectDisposedException) { check(true, "disposed state rejects new publishes"); }
    }

    private static async Task CheckIncomingQueue(Action<bool, string> check)
    {
        foreach (var mode in new[] { "disable", "replace", "delete" })
            await CheckIncomingQueueCase(check, mode);
    }

    private static async Task CheckIncomingQueueCase(Action<bool, string> check, string mode)
    {
        var sessions = new FakeSessions();
        using var state = new DdsStateService(sessions, NullLogger<DdsStateService>.Instance);
        Action<DdsReceivedSample>? deliver = null;
        state.StartSubscription("queue", "Incoming", "Type", callback => { deliver = callback; return new CountingHandle(); });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writes = 0;
        await using var triggers = new DdsTriggerService(state, sessions, NullLogger<DdsTriggerService>.Instance, _ =>
        {
            Interlocked.Increment(ref writes);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Missing test release");
            return new(true, null);
        }, 2);
        var trigger = new DdsTrigger { SessionId = "queue", Type = DdsTriggerType.OnIncoming,
            Enabled = true, TopicName = "Outgoing", MinFireIntervalMs = 0, MaxFiresPerMinute = 0 };
        triggers.Add(trigger);
        var sample = new DdsReceivedSample("{}", DateTime.UtcNow, null);
        var receive = Task.Run(() => deliver!(sample));
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = 0;
        try
        {
            await receive.WaitAsync(TimeSpan.FromSeconds(2));
            check(entered.Wait(TimeSpan.FromSeconds(2)) && writes == 1,
                "receive callback returns while automatic-response publish remains blocked");
            deliver!(sample); deliver(sample); deliver(sample);
            check(trigger.Errors == 1 && trigger.LastError!.Contains("queue is full") && state.GetInboundCount("queue") == 4,
                "bounded response queue reports overflow without blocking or losing inbound display samples");
            if (mode == "disable")
            {
                triggers.SetEnabled(trigger.Id, false);
                triggers.SetEnabled(trigger.Id, true);
            }
            else if (mode == "replace")
                triggers.Update(new DdsTrigger { Id = trigger.Id, SessionId = "queue", Type = DdsTriggerType.OnIncoming,
                    Enabled = true, TopicName = "Outgoing" });
            else sessions.RaiseDeleting("queue");
            triggers.Changed += () => { if (Interlocked.Increment(ref completed) == 3) drained.TrySetResult(); };
        }
        finally { release.Set(); }
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(3));
        check(writes == 1, $"{mode} invalidates previously queued responses");
        if (mode != "disable") return;

        release.Reset(); entered.Reset();
        deliver!(sample);
        Task shutdown;
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Response worker did not restart");
            deliver(sample); deliver(sample);
            shutdown = triggers.DisposeAsync().AsTask();
        }
        finally { release.Set(); }
        await shutdown;
        check(writes == 2, "shutdown discards queued automatic responses and joins in-flight response");
    }

    private sealed class CountingHandle(Action? onDispose = null) : IDisposable
    {
        public int DisposeCount;
        public void Dispose() { Interlocked.Increment(ref DisposeCount); onDispose?.Invoke(); }
    }

    private sealed class AsyncHandle(Task? completion = null) : IAsyncDisposable
    {
        public int Disposals;
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            if (completion is not null) await completion;
        }
    }

    private sealed class FakeSessions : IDdsSessionService
    {
        private readonly ConcurrentDictionary<string, byte> _deleted = new();
        public event Action<string>? SessionDeleting;
        public void RaiseDeleting(string id) { _deleted[id] = 0; SessionDeleting?.Invoke(id); }
        public DdsSession Create(DdsSessionCreateRequest request) => throw new NotSupportedException();
        // 실제 서비스의 Get은 활성 세션만 반환한다. 테스트 대역도 같은 상태 계약을 따른다.
        public DdsSession? Get(string id) => _deleted.ContainsKey(id) ? null : new DdsSession { SessionId = id, State = DdsSessionState.Active };
        public DdsParticipantHost? GetHost(string id) => null;
        public IReadOnlyList<DdsSession> GetByProfileId(string id) => [];
        public Task DeleteAsync(string id) { RaiseDeleting(id); return Task.CompletedTask; }
        public IReadOnlyList<DdsSession> GetAll() => [];
    }
}

// Test-only CLR schemas are never registered in the production module allowlist.
namespace DdsModules_RegressionA.Common { public sealed class Status { public string Text { get; set; } = string.Empty; } }
namespace DdsModules_RegressionB.Common { public sealed class Status { public string Text { get; set; } = string.Empty; } }
