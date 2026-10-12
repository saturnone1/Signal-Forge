using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ASAP.Dds;
using ASAP.Models.Dds;
using ASAP.Models.Session;
using ASAP.Services;
using DdsAmbassador.DDSClient;
using DdsAmbassador.Messages;
using DdsModules_DDSSim.MSG;
using Microsoft.Extensions.Logging.Abstractions;

// 실제 RTI 검증은 network=none인 별도 컨테이너에서 실행한다. 기존 앱·데이터·운영 도메인은 사용하지 않는다.
internal static class RuntimeStressProbe
{
    private const string BaseQos = """
    <dds><qos_library name="AmbassadorProfiles"><qos_profile name="P" is_default_qos="true">
    <participant_qos><transport_builtin><mask>UDPv4</mask></transport_builtin>
    <discovery><initial_peers><element>udpv4://127.0.0.1</element></initial_peers><multicast_receive_addresses/></discovery>
    <property><value><element><name>dds.transport.UDPv4.builtin.parent.allow_interfaces</name><value>127.0.0.1</value></element></value></property>
    </participant_qos>
    <datawriter_qos><durability><kind>VOLATILE_DURABILITY_QOS</kind></durability><reliability><kind>RELIABLE_RELIABILITY_QOS</kind></reliability><history><kind>KEEP_ALL_HISTORY_QOS</kind></history></datawriter_qos>
    <datareader_qos><durability><kind>VOLATILE_DURABILITY_QOS</kind></durability><reliability><kind>RELIABLE_RELIABILITY_QOS</kind></reliability><history><kind>KEEP_ALL_HISTORY_QOS</kind></history></datareader_qos>
    </qos_profile></qos_library></dds>
    """;
    private static string TransportMask => Environment.GetEnvironmentVariable("DDS_STRESS_TRANSPORT") ?? "UDPv4";
    private static string Qos => TransportMask switch
    {
        "UDPv4" => BaseQos,
        "UDPv4|SHMEM" => BaseQos.Replace("<mask>UDPv4</mask>", "<mask>UDPv4|SHMEM</mask>", StringComparison.Ordinal),
        _ => throw new InvalidOperationException("허용되지 않은 격리 테스트 전송 설정입니다."),
    };
    private const string Topics = """[{"TopicName":"TimeTickInformation","TypeName":"MSG::TimeTickInformation","QosProfileName":"P","Direction":"Both"}]""";

    public static async Task Run()
    {
        var checks = 0;
        var metrics = new Dictionary<string, object>();
        metrics["transport_mask"] = TransportMask;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("실패: " + name);
            checks++;
            Console.WriteLine("통과: " + name);
        }
        var directory = Path.Combine(Path.GetTempPath(), "dds-runtime-stress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "qos.xml");
        await File.WriteAllTextAsync(path, Qos);
        DdsClientOptions Options(int domain, int timeoutMs = 10000) => new()
        {
            MessageModules = [DDSSimMessages.Module], DomainId = domain, QosProfilesXmlPath = path,
            ParticipantQosProfile = "AmbassadorProfiles::P", Topics = [Topic.Both<TimeTickInformation>("P")],
            ShutdownTimeout = TimeSpan.FromMilliseconds(timeoutMs), LogLevel = DdsLogLevel.None,
        };
        try
        {
            var factory = new DdsParticipantHostFactory(NullLoggerFactory.Instance);
            var module = BuiltMessageRegistry.Get("DDSSim");
            await using (var sessions = new DdsSessionService(factory, NullLogger<DdsSessionService>.Instance))
            using (var state = new DdsStateService(sessions, NullLogger<DdsStateService>.Instance))
            using (var peer = DdsClient.Connect(Options(191)))
            {
                var publisher = peer.CreatePublisher<TimeTickInformation>();
                var timer = Stopwatch.StartNew();
                publisher.Publish(new TimeTickInformation { Header = { TimeTick = ulong.MaxValue } });
                metrics["publish_without_reader_ms"] = timer.Elapsed.TotalMilliseconds;
                Check(timer.Elapsed < TimeSpan.FromSeconds(2), "구독자 없이 발행이 반환됨");

                DdsSessionCreateRequest Request(int domain, string name) => new()
                {
                    Name = name, ProfileId = name, ProfileName = name, ProfileUpdatedAtUtc = DateTimeOffset.UtcNow,
                    ModuleName = "DDSSim", QosLibraryName = "AmbassadorProfiles",
                    Transport = new() { DomainId = domain, ParticipantQosProfile = "AmbassadorProfiles::P" },
                    DdsSimXmlContent = module.ContractXml, TopicSettingsJsonContent = Topics, QosProfilesXmlContent = Qos,
                };
                var active = Enumerable.Range(0, 4).Select(i => sessions.Create(Request(191, "수신기-" + i))).ToArray();
                var isolated = sessions.Create(Request(192, "다른-도메인"));
                var observations = active.ToDictionary(s => s.SessionId, _ => new Observation());
                state.SampleReceived += (info, sample) =>
                {
                    if (!observations.TryGetValue(info.SessionId, out var values)) return;
                    using var json = JsonDocument.Parse(sample.JsonData);
                    var id = json.RootElement.GetProperty("Header").GetProperty("TimeTick").GetUInt64();
                    values.Record(id, sample);
                };
                var readers = active.Select(s => state.StartSubscription(s.SessionId, "TimeTickInformation", "MSG::TimeTickInformation", "P")).ToArray();
                var other = state.StartSubscription(isolated.SessionId, "TimeTickInformation", "MSG::TimeTickInformation", "P");
                // 테스트의 측정 시작점만 맞춘다. 라이브러리의 일반 Publish에 대기 조건을 추가하지 않는다.
                publisher.WaitForSubscribers(4, TimeSpan.FromSeconds(20));
                for (ulong id = 1; id <= 4; id++) publisher.Publish(new TimeTickInformation { Header = { TimeTick = id } });
                await Until(() => observations.Values.All(v => v.Count == 4));
                Check(observations.Values.All(v => Enumerable.Range(1, 4).All(i => v.Contains((ulong)i))), "4개 메시지를 4개 독립 가입자가 모두 수신");
                Check(observations.Values.All(v => !v.Contains(ulong.MaxValue)), "VOLATILE: 가입 이전 메시지 재생 없음");

                const int count = 10000;
                var process = Process.GetCurrentProcess();
                process.Refresh();
                var memoryBefore = process.WorkingSet64;
                var threadsBefore = process.Threads.Count;
                var startedUtc = DateTime.UtcNow.Ticks;
                var started = Stopwatch.GetTimestamp();
                timer.Restart();
                for (ulong id = 5; id <= count + 4; id++) publisher.Publish(new TimeTickInformation { Header = { TimeTick = id } });
                var writeMs = timer.Elapsed.TotalMilliseconds;
                var ackStarted = Stopwatch.GetTimestamp();
                publisher.WaitForAcknowledgments(TimeSpan.FromSeconds(30));
                var ackMs = Stopwatch.GetElapsedTime(ackStarted).TotalMilliseconds;
                await Until(() => observations.Values.All(v => v.Count == count + 4), 45);
                metrics["load_messages"] = count;
                metrics["subscribers"] = 4;
                metrics["write_ms"] = writeMs;
                metrics["delivery_ms"] = timer.Elapsed.TotalMilliseconds;
                metrics["ack_wait_ms"] = ackMs;
                metrics["last_callback_ms"] = observations.Values.Max(v => Stopwatch.GetElapsedTime(started, v.CompletedAt).TotalMilliseconds);
                metrics["last_native_reception_ms"] = observations.Values.Max(v => (v.NativeReceptionTicks - startedUtc) / (double)TimeSpan.TicksPerMillisecond);
                metrics["write_messages_per_second"] = count * 1000 / writeMs;
                metrics["received_per_subscriber"] = observations.Values.Select(v => v.Count).ToArray();
                var duplicates = observations.Values.Sum(v => v.Duplicates);
                var unordered = observations.Values.Sum(v => v.Unordered);
                metrics["duplicates"] = duplicates;
                metrics["out_of_order"] = unordered;
                process.Refresh();
                metrics["working_set_delta_bytes"] = process.WorkingSet64 - memoryBefore;
                metrics["threads_before_load"] = threadsBefore;
                metrics["threads_after_load"] = process.Threads.Count;
                Check(duplicates == 0 && unordered == 0 && observations.Values.All(v => Enumerable.Range(1, count + 4).All(i => v.Contains((ulong)i))), "부하: 각 가입자 10004개 누락·중복·순서 오류 없음");
                Check(observations.Values.Sum(v => v.MetadataMissing) == 0, "실제 DDS 발행 시각과 수신 지연 메타데이터 전달");
                Check(other.ReceivedCount == 0, "다른 도메인 수신기에는 메시지가 전달되지 않음");
                Check(active.All(s => state.SnapshotInbound(s.SessionId, 100).Count == 50), "Signal Forge 수신 이력은 가입자당 50개로 제한됨");

                var response = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var responseReader = peer.Subscribe<TimeTickInformation>(sample =>
                {
                    if (sample.Header.TimeTick == 10005) response.TrySetResult(sample.Header.TimeTick);
                });
                sessions.GetHost(active[0].SessionId)!.WaitForSubscribers("TimeTickInformation", "MSG::TimeTickInformation", "P", 5, TimeSpan.FromSeconds(20));
                var result = state.Publish(active[0].SessionId, "TimeTickInformation", "MSG::TimeTickInformation", "P", """{"Header":{"TimeTick":10005}}""");
                Check(result.Success && await response.Task.WaitAsync(TimeSpan.FromSeconds(15)) == 10005, "Signal Forge JSON 발행 → 실제 DDSClient 수신");
                foreach (var reader in readers) state.StopSubscription(reader.SubscriptionId);
                state.StopSubscription(other.SubscriptionId);
                timer.Restart();
                foreach (var session in active.Append(isolated)) await sessions.DeleteAsync(session.SessionId);
                metrics["adapter_sessions_close_ms"] = timer.Elapsed.TotalMilliseconds;
                Check(sessions.GetAll().Count == 0, "실제 RTI 세션 5개 정상 종료");
            }

            using (var client = DdsClient.Connect(Options(193, 250)))
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var subscription = client.Subscribe<TimeTickInformation>(_ => { entered.Set(); release.Wait(); });
                var publisher = client.CreatePublisher<TimeTickInformation>();
                publisher.WaitForSubscribers(1, TimeSpan.FromSeconds(20));
                publisher.Publish(new TimeTickInformation());
                Check(entered.Wait(TimeSpan.FromSeconds(10)), "실제 RTI 수신 콜백 정지 재현");
                try
                {
                    var timer = Stopwatch.StartNew();
                    await Task.Run(client.Close).WaitAsync(TimeSpan.FromSeconds(3));
                    metrics["blocked_callback_close_ms"] = timer.Elapsed.TotalMilliseconds;
                    Check(timer.Elapsed < TimeSpan.FromSeconds(2) && client.ShutdownState == DdsShutdownState.Closing,
                        "콜백 정지 중 Close는 제한 시간 내 반환하고 상태는 Closing 유지");
                    Check(!client.WaitForShutdown(TimeSpan.FromMilliseconds(50)), "미완료 종료를 Closed로 오인하지 않음");
                }
                finally { release.Set(); }
                Check(await Task.Run(() => client.WaitForShutdown(TimeSpan.FromSeconds(10))), "콜백 해제 후 네이티브 정리 완료");
                subscription.Dispose();
            }
            var lifecycleProcess = Process.GetCurrentProcess();
            lifecycleProcess.Refresh();
            var baselineThreads = lifecycleProcess.Threads.Count;
            var closeTimes = new List<double>();
            for (var cycle = 0; cycle < 10; cycle++)
            {
                using var restarted = DdsClient.Connect(Options(194, 2000));
                var reader = restarted.Subscribe<TimeTickInformation>(_ => { });
                restarted.CreatePublisher<TimeTickInformation>();
                var timer = Stopwatch.StartNew();
                reader.Dispose();
                restarted.Close();
                Check(await Task.Run(() => restarted.WaitForShutdown(TimeSpan.FromSeconds(5))), $"재연결·종료 {cycle + 1}/10: 네이티브 정리 완료");
                closeTimes.Add(timer.Elapsed.TotalMilliseconds);
            }
            lifecycleProcess.Refresh();
            metrics["reconnect_cycles"] = 10;
            metrics["reconnect_close_ms"] = closeTimes;
            metrics["threads_before_reconnects"] = baselineThreads;
            metrics["threads_after_reconnects"] = lifecycleProcess.Threads.Count;
            Check(lifecycleProcess.Threads.Count <= baselineThreads + 16, "10회 재연결 후 스레드 수가 누적 증가하지 않음");

            // 네이티브 SDK 자체를 손상시키지 않고, 어댑터의 종료 실패 경로에 명시적으로 예외를 주입한다.
            {
                var sessions = new DdsSessionService(null!, NullLogger<DdsSessionService>.Instance);
                sessions.CreateOwnedSession(new DdsSession { SessionId = "강제-정리-실패" }, () => new FailingCleanup());
                if (sessions.Get("강제-정리-실패") is null) throw new InvalidOperationException("실패 주입 전 활성 세션이 없습니다.");
                try { await sessions.DeleteAsync("강제-정리-실패"); throw new Exception("종료 실패가 숨겨졌습니다."); }
                catch (InvalidOperationException error) when (error.Message == "강제 주입한 정리 실패")
                {
                    var failed = sessions.GetAll().Single();
                    Check(failed.State == DdsSessionState.Faulted && failed.CleanupError == error.Message && sessions.Get(failed.SessionId) is null,
                        "종료 실패 주입: Faulted 상태·오류 보존, 신규 작업 차단");
                }
                // 실패 상태를 소유하는 테스트 전용 서비스는 다시 Dispose하면 같은 오류를 보고한다.
                try { await sessions.DisposeAsync(); } catch (InvalidOperationException) { }
            }
            metrics["passed_checks"] = checks;
            var report = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(report);
            if (Directory.Exists("/results")) await File.WriteAllTextAsync("/results/runtime-stress-report.json", report);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public static async Task RunDdsOnly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dds-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "qos.xml");
        await File.WriteAllTextAsync(path, Qos);
        DdsClientOptions Options() => new()
        {
            MessageModules = [DDSSimMessages.Module], DomainId = 195, QosProfilesXmlPath = path,
            ParticipantQosProfile = "AmbassadorProfiles::P", Topics = [Topic.Both<TimeTickInformation>("P")],
        };
        var clients = new List<DdsClient>();
        var handles = new List<IDisposable>();
        try
        {
            using var publisherClient = DdsClient.Connect(Options());
            var publisher = publisherClient.CreatePublisher<TimeTickInformation>();
            var observations = Enumerable.Range(0, 4).Select(_ => new Observation()).ToArray();
            foreach (var observation in observations)
            {
                var client = DdsClient.Connect(Options());
                clients.Add(client);
                handles.Add(client.SubscribeWithInfoBorrowed<TimeTickInformation>((sample, info) =>
                    observation.Record(sample.Header.TimeTick, info.ReceivedAtUtc.Ticks, info.SourceTimestampNs > 0)));
            }
            publisher.WaitForSubscribers(4, TimeSpan.FromSeconds(20));
            for (ulong id = 1; id <= 4; id++) publisher.Publish(new TimeTickInformation { Header = { TimeTick = id } });
            await Until(() => observations.All(o => o.Count == 4));
            var startedUtc = DateTime.UtcNow.Ticks;
            var started = Stopwatch.GetTimestamp();
            for (ulong id = 5; id <= 10004; id++) publisher.Publish(new TimeTickInformation { Header = { TimeTick = id } });
            var writeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var ackStarted = Stopwatch.GetTimestamp();
            publisher.WaitForAcknowledgments(TimeSpan.FromSeconds(30));
            var ackMs = Stopwatch.GetElapsedTime(ackStarted).TotalMilliseconds;
            await Until(() => observations.All(o => o.Count == 10004), 45);
            if (observations.Any(o => o.Duplicates != 0 || o.Unordered != 0 || Enumerable.Range(1, 10004).Any(id => !o.Contains((ulong)id))))
                throw new InvalidOperationException("DDS 단독 수신 무결성 오류");
            var report = JsonSerializer.Serialize(new
            {
                path = "dds-only", write_ms = writeMs, ack_wait_ms = ackMs,
                last_callback_ms = observations.Max(o => Stopwatch.GetElapsedTime(started, o.CompletedAt).TotalMilliseconds),
                last_native_reception_ms = observations.Max(o => (o.NativeReceptionTicks - startedUtc) / (double)TimeSpan.TicksPerMillisecond),
                received_per_subscriber = observations.Select(o => o.Count).ToArray(), duplicates = 0, out_of_order = 0,
            }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(report);
            if (Directory.Exists("/results")) await File.WriteAllTextAsync("/results/dds-only-report.json", report);
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
            foreach (var client in clients) client.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    // 가입자마다 전용 수신 스레드 하나가 기록한다. 공통 잠금·ConcurrentDictionary 할당을 측정 경로에서 제거한다.
    private sealed class Observation
    {
        private readonly bool[] _seen = new bool[10006];
        private int _count;
        private ulong _last;
        private bool _preSubscription;
        public int Count => Volatile.Read(ref _count);
        public int Duplicates;
        public int Unordered;
        public int MetadataMissing;
        public long CompletedAt;
        public long NativeReceptionTicks;
        public bool Contains(ulong id) => id == ulong.MaxValue ? _preSubscription : id < (ulong)_seen.Length && _seen[(int)id];
        public void Record(ulong id, DdsSampleEntry sample) => Record(id, sample.ReceivedAt.Ticks, sample.SourceTimestampNs > 0 && sample.ReceiveLatencyMs is not null);
        public void Record(ulong id, long nativeTicks, bool metadataAvailable)
        {
            if (id == ulong.MaxValue) { _preSubscription = true; return; }
            if (id >= (ulong)_seen.Length) throw new InvalidOperationException("범위 밖 테스트 메시지입니다.");
            var fresh = !_seen[(int)id];
            if (!fresh) Duplicates++;
            else { _seen[(int)id] = true; }
            if (id <= _last) Unordered++;
            _last = id;
            if (!metadataAvailable) MetadataMissing++;
            NativeReceptionTicks = Math.Max(NativeReceptionTicks, nativeTicks);
            CompletedAt = Stopwatch.GetTimestamp();
            if (fresh) Volatile.Write(ref _count, _count + 1);
        }
    }

    private sealed class FailingCleanup : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("강제 주입한 정리 실패"));
    }
    private static async Task Until(Func<bool> ready, int seconds = 20)
    {
        var deadline = Stopwatch.StartNew();
        while (!ready())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("DDS 수신 완료 대기 시간 초과");
            await Task.Delay(10);
        }
    }
}
