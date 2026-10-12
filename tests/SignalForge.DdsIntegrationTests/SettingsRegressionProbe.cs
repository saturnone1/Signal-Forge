using ASAP.Dds;
using ASAP.Models.Dds;
using ASAP.Models.Session;
using ASAP.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SettingsRegressionProbe
{
    public static async Task RunRti()
    {
        const string qos = """
        <dds><qos_library name="Independent"><qos_profile name="P">
        <participant_qos><transport_builtin><mask>UDPv4</mask></transport_builtin>
        <discovery><initial_peers><element>udpv4://127.0.0.1</element></initial_peers><multicast_receive_addresses/></discovery>
        <property><value><element><name>dds.transport.UDPv4.builtin.parent.allow_interfaces</name><value>127.0.0.1</value></element></value></property>
        </participant_qos>
        <datawriter_qos><reliability><kind>RELIABLE_RELIABILITY_QOS</kind></reliability><history><kind>KEEP_ALL_HISTORY_QOS</kind></history></datawriter_qos>
        <datareader_qos><reliability><kind>RELIABLE_RELIABILITY_QOS</kind></reliability><history><kind>KEEP_ALL_HISTORY_QOS</kind></history></datareader_qos>
        </qos_profile></qos_library></dds>
        """;
        const string topics = """[{"TopicName":"RuntimeReplacement","TypeName":"Telemetry::Heartbeat","QosProfileName":"P","Direction":"Both"}]""";
        var moduleName = System.Environment.GetEnvironmentVariable("DDS_MODULE_PROBE_NAME") ?? "Telemetry";
        var module = BuiltMessageRegistry.Get(moduleName);
        var factory = new DdsParticipantHostFactory(NullLoggerFactory.Instance);
        await using var sender = factory.Create(new DdsTransportSettings { DomainId = 198 }, module.ContractXml, topics, qos, moduleName, "Independent");
        await using var receiver = factory.Create(new DdsTransportSettings { DomainId = 198 }, module.ContractXml, topics, qos, moduleName, "Independent");
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = receiver.Subscribe("RuntimeReplacement", "Telemetry::Heartbeat", "Independent::P",
            sample => { using var document = System.Text.Json.JsonDocument.Parse(sample.Json);
                if (document.RootElement.GetProperty("Counter").GetUInt64() == 42) arrived.TrySetResult(); });
        sender.PreparePublisher("RuntimeReplacement", "Telemetry::Heartbeat", "Independent::P");
        // 검사에서만 발견 완료를 기다린다. 일반 발행 경로에 대기 조건을 추가하지 않는다.
        sender.WaitForSubscribers("RuntimeReplacement", "Telemetry::Heartbeat", "Independent::P", 1, TimeSpan.FromSeconds(10));
        using var payload = sender.CreateSample("Telemetry::Heartbeat");
        DdsJsonConverter.ApplyJson(payload, """{"Counter":42}""");
        sender.Publish("RuntimeReplacement", "Telemetry::Heartbeat", "Independent::P", payload);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine("외부 모듈 실제 DDS 송수신 통과: " + module.Module.Name + ":" + module.Module.Version);
    }

    public static async Task Run()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("통과: " + name); }
        const string qos = """<dds><qos_library name="Independent"><qos_profile name="P"><datawriter_qos><reliability><kind>RELIABLE_RELIABILITY_QOS</kind></reliability></datawriter_qos></qos_profile></qos_library></dds>""";
        var module = BuiltMessageRegistry.Get("Telemetry");
        var profile = new DdsXmlProfile { Name = "외부 QoS 검증", ModuleName = "Telemetry", DdsSimXml = module.ContractXml,
            QosProfilesXml = qos, QosLibraryName = "Independent", ParticipantQosProfile = "Independent::P",
            TopicSettingsJson = DdsTopicSettings.Write([new DdsTopicConfig { TopicName = "Alias", TypeName = "Telemetry::Heartbeat",
                Direction = DdsTopicDirection.Both, QosProfileName = "P" }]) };
        Check(DdsProfileService.Validate(profile).TopicCount == 1, "Topics XML 없이 JSON 목록 검증");
        var editor = DdsConfigProfileEditor.Parse(profile.TopicSettingsJson, qos, "Independent", module);
        editor.QosProfiles[0].WriterReliability = DdsConfigProfileEditor.BestEffort;
        var applied = DdsConfigProfileEditor.Apply(profile.TopicSettingsJson, qos, editor);
        Check(applied.TopicSettingsJson.TrimStart().StartsWith('[') && applied.QosProfilesXml.Contains("BEST_EFFORT"), "구조 편집은 토픽 JSON과 외부 QoS XML 생성");
        var imported = DdsProfileFiles.CreateProfile("가져온 설정", new Dictionary<string,string>
        {
            [DdsProfileFiles.ConnectionFileName] = DdsProfileFiles.ExportConnection(profile),
            [DdsProfileFiles.QosProfilesFileName] = qos,
        });
        Check(imported.ModuleName == "Telemetry" && imported.QosLibraryName == "Independent" &&
            DdsTopicSettings.Read(imported.TopicSettingsJson)[0].TopicName == "Alias", "두 파일 가져오기에서 모듈·QoS·별칭 보존");
        var migrated = DdsTopicSettings.Migrate("""<topics><topic name="Alias" type_name="Telemetry::Heartbeat" qos_profile="P" direction="Both"/></topics>""");
        Check(DdsTopicSettings.Read(migrated)[0].TopicName == "Alias", "구형 Topics XML은 JSON으로 마이그레이션");
        var root = Path.Combine(Path.GetTempPath(), "signal-forge-settings-" + Guid.NewGuid().ToString("N"));
        var sessions = new Sessions();
        var store = new DdsProfileService(new Environment(root), new ConfigurationBuilder().Build(), sessions,
            NullLogger<DdsProfileService>.Instance);
        var catalog = new DdsProfileCatalog { Profiles = [profile] };
        await store.SaveAsync(catalog);
        var loaded = await store.LoadAsync();
        Check(loaded.Version == 4 && Directory.EnumerateFiles(root, "connection.json", SearchOption.AllDirectories).Any() &&
            !Directory.EnumerateFiles(root, "topics.xml", SearchOption.AllDirectories).Any(), "새 저장소는 Topics XML 파일을 생성하지 않음");
        var oldQos = profile.QosProfilesXml;
        sessions.Active = new DdsSession { ProfileId = profile.Id, State = DdsSessionState.Active,
            QosProfilesXmlContent = System.Text.Encoding.UTF8.GetBytes(oldQos) };
        loaded.Profiles[0].QosProfilesXml = applied.QosProfilesXml;
        loaded.Profiles[0].UpdatedAtUtc = DateTimeOffset.UtcNow;
        await store.SaveAsync(loaded);
        var changed = await store.LoadAsync();
        Check(changed.Profiles[0].QosProfilesXml.Contains("BEST_EFFORT"), "활성 세션이 있어도 빌드 후 QoS 편집·저장 가능");
        Check(System.Text.Encoding.UTF8.GetString(sessions.Active.QosProfilesXmlContent!) == oldQos,
            "기존 세션의 생성 시점 QoS 스냅샷은 유지");
        var legacyRoot = Path.Combine(root, "legacy");
        var legacyDirectory = Path.Combine(legacyRoot, "data", "dds-profiles", profile.Id);
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "DDSSim.xml"), module.ContractXml);
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "topics.xml"), """<topics><topic name="Alias" type_name="Telemetry::Heartbeat" qos_profile="P" direction="Both"/></topics>""");
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "qos_profiles.xml"), qos);
        await File.WriteAllTextAsync(Path.Combine(legacyRoot, "data", "dds-profiles.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 3, revision = 1, profiles = new[] { new { id = profile.Id, name = "이전 저장소", moduleName = "Telemetry", qosLibraryName = "Independent" } },
        }));
        var legacyStore = new DdsProfileService(new Environment(legacyRoot), new ConfigurationBuilder().Build(), new Sessions(),
            NullLogger<DdsProfileService>.Instance);
        var legacyCatalog = await legacyStore.LoadAsync();
        Check(legacyCatalog.Version == 4 && DdsTopicSettings.Read(legacyCatalog.Profiles[0].TopicSettingsJson)[0].TopicName == "Alias" &&
            Directory.EnumerateFiles(legacyRoot, "connection.json", SearchOption.AllDirectories).Any(), "실제 v3 파일 저장소를 v4 JSON 연결 설정으로 변환");
        Check(File.Exists(Path.Combine(legacyDirectory, "topics.xml")) && File.Exists(Path.Combine(legacyRoot, "data", "dds-profiles.json.bak")),
            "마이그레이션 전 파일과 목록 백업 보존");
        Console.WriteLine($"설정 회귀 검사 {count}개 통과. 확인용 저장소: {root}");
    }
    private sealed class Sessions : IDdsSessionService
    {
        public DdsSession? Active;
        public event Action<string>? SessionDeleting { add { } remove { } }
        public DdsSession? Get(string id) => Active;
        public DdsParticipantHost? GetHost(string id) => null;
        public DdsSession Create(DdsSessionCreateRequest request) => throw new NotSupportedException();
        public IReadOnlyList<DdsSession> GetByProfileId(string id) => Active?.ProfileId == id ? [Active] : [];
        public IReadOnlyList<DdsSession> GetAll() => Active is null ? [] : [Active];
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }
    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "SignalForge.SettingsProbe";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
