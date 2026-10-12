using System.Xml.Linq;
using System.Text.Json;
using ASAP.Dds;
using ASAP.Models.Dds;
using ASAP.Models.Session;
using ASAP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

var xml = """
<dds><types><module name="M">
  <typedef name="Ids" type="int32" sequenceMaxLength="8" />
  <union name="Choice"><discriminator type="int32"/><case><caseDiscriminator value="0"/><member name="number" type="int32"/></case></union>
  <struct name="Root"><member name="ids" type="nonBasic" nonBasicTypeName="M::Ids"/><member name="choice" type="nonBasic" nonBasicTypeName="M::Choice"/></struct>
</module></types></dds>
""";
var types = DdsTypeParser.Parse(xml);
Check(DdsJsonConverter.NormalizeJson("\"Header\":{\"TimeTick\":0},\"Value\":1") ==
      "{\"Header\":{\"TimeTick\":0},\"Value\":1}",
      "RTI rootless member JSON normalized");
Check(DdsJsonConverter.NormalizeJson("{\"Value\":1}") == "{\"Value\":1}",
      "valid JSON preserved");
Check(types["M::Ids"].AliasIsSequence && types["M::Ids"].AliasSequenceMaxLength == 8, "typedef collection metadata");
Check(types["M::Choice"].UnionCases.Single().Labels.Single() == "0", "union case metadata");
var topicsXml = """
<topics><topic custom="legacy" name="T" qos_profile="P" direction="Both"><custom>kept</custom></topic></topics>
""";
var qosProfilesXml = """
<dds><qos_library name="AmbassadorProfiles"><qos_profile name="P"/></qos_library></dds>
""";
var config = DdsConfigProfileEditor.Parse(topicsXml, qosProfilesXml);
var applied = DdsConfigProfileEditor.Apply(topicsXml, qosProfilesXml, config);
var topic = DdsTopicSettings.Read(applied.TopicSettingsJson).Single();
Check(topic.TopicName == "T", "코드 등록용 토픽 JSON 정규화");
var parsedConfig = DdsConfigParser.Parse(applied.TopicSettingsJson, applied.QosProfilesXml);
Check(parsedConfig.Topics.Single().TypeName == "MSG::T", "DDSClient topic type mapping");
ExpectFailure(
    () => DdsConfigParser.Parse("<topics><topic name=\"T\" qos_profile=\"P\" direction=\"1\"/></topics>", qosProfilesXml),
    "DDSClient direction names are exact");
config.QosProfiles[0].WriterHistoryDepth = 0;
ExpectFailure(() => DdsConfigProfileEditor.ValidateState(config), "invalid history depth");

var sessions = new FakeSessions();
using var state = new DdsStateService(sessions, NullLogger<DdsStateService>.Instance);
for (var i = 0; i < 80; i++) state.RecordExternalPublish("s", "t", "M::Root", new string('x', 300_000), true);
Check(state.SnapshotOutbound("s", 100).Count == 50, "bounded outbound history");
Check(state.GetOutboundCount("s") == 80, "outbound total survives bounded history");
Check(state.SnapshotOutbound("s", 1)[0].JsonPayload.Length < 70_000, "bounded retained payload");
var receivedAt = DateTime.UtcNow;
var receivedAtNs = new DateTimeOffset(receivedAt).ToUnixTimeMilliseconds() * 1_000_000L
                   + receivedAt.Ticks % TimeSpan.TicksPerMillisecond * 100L;
var receiveLatency = DdsStateService.CalculateReceiveLatencyMs(receivedAt, receivedAtNs - 12_500_000L);
Check(receiveLatency is >= 12.49 and <= 12.51, "DDS source timestamp receive latency");
Check(DdsStateService.CalculateReceiveLatencyMs(receivedAt, 0) == null, "missing DDS source timestamp latency");
sessions.RaiseDeleting("s");
Check(state.SnapshotOutbound("s", 100).Count == 0, "session history cleanup");
Check(state.GetOutboundCount("s") == 0, "session counters cleanup");

var repositoryDirectory = new DirectoryInfo(AppContext.BaseDirectory);
while (repositoryDirectory is not null && !File.Exists(Path.Combine(repositoryDirectory.FullName, "ASAP.csproj")))
    repositoryDirectory = repositoryDirectory.Parent;
var repositoryRoot = repositoryDirectory?.FullName ?? throw new InvalidOperationException("Signal-Forge repository root not found.");
var sampleTypes = File.ReadAllText(Path.Combine(repositoryRoot, "samples", "dds", "DDSSim.xml"));
var qosXml = File.ReadAllText(Path.Combine(repositoryRoot, "samples", "dds", "qos_profiles.xml"));
var sampleTopics = DdsTopicSettings.Write([new DdsTopicConfig { TopicName = "TimeTickInformation",
    TypeName = "MSG::TimeTickInformation", QosProfileName = "ReliableRealtime", Direction = DdsTopicDirection.Both }]);
DdsProfileService.Validate(new DdsXmlProfile
{
    Name = "DDSClient contract",
    DdsSimXml = sampleTypes,
    TopicSettingsJson = sampleTopics,
    QosProfilesXml = qosXml,
});
Check(DdsProfileFiles.RequiredFileNames.SequenceEqual(
          new[] { "connection.json", "qos_profiles.xml" }),
      "연결 JSON과 QoS XML 파일 이름");
var importedFiles = DdsProfileFiles.CreateProfile("imported contract", new Dictionary<string, string>
{
    ["connection.json"] = DdsProfileFiles.ExportConnection(new DdsXmlProfile { ModuleName = "DDSSim", TopicSettingsJson = sampleTopics }),
    ["qos_profiles.xml"] = qosXml,
});
Check(importedFiles.DdsSimXml == sampleTypes && importedFiles.TopicSettingsJson == sampleTopics &&
      importedFiles.QosProfilesXml == qosXml,
      "JSON 연결 설정과 QoS 가져오기");
ExpectFailure(
    () => DdsProfileFiles.CreateProfile("missing file", new Dictionary<string, string>
    {
        ["connection.json"] = "{}",
    }),
    "두 설정 파일이 필요함");
var sampleTopicsDocument = XDocument.Parse("""<topics><topic name="TimeTickInformation" qos_profile="ReliableRealtime" direction="Both"/></topics>""");
var sampleQosDocument = XDocument.Parse(qosXml);
var legacyConfig = new XDocument(new XElement("dds-ambassador-config",
    new XElement("dds", sampleQosDocument.Root!.Elements().Select(element => new XElement(element))),
    new XElement("topics", sampleTopicsDocument.Root!.Elements("topic").Select(element => new XElement("topic",
        new XElement("topic_name", element.Attribute("name")!.Value),
        new XElement("type_name", $"MSG::{element.Attribute("name")!.Value}"),
        new XElement("direction", element.Attribute("direction")!.Value),
        new XElement("qos_profile", element.Attribute("qos_profile")!.Value)))))).ToString();
var legacyProfile = new DdsXmlProfile
{
    Name = "legacy migration",
    LegacyTypesXml = sampleTypes,
    LegacyConfigXml = legacyConfig,
};
DdsProfileService.Validate(legacyProfile);
Check(legacyProfile.DdsSimXml.Length > 0 && legacyProfile.TopicSettingsJson.Length > 0 &&
      legacyProfile.QosProfilesXml.Length > 0 && legacyProfile.LegacyConfigXml == null,
      "구형 프로필 JSON 마이그레이션");
using var loggerFactory = LoggerFactory.Create(_ => { });
// DDS participants are opt-in; ordinary smoke tests never join a production domain.
if (args.Contains("--rti", StringComparer.Ordinal))
{
    await using var host = new DdsParticipantHostFactory(loggerFactory).Create(
        new DdsTransportSettings { DomainId = 191 }, sampleTypes, sampleTopics, qosXml);
}

var storeDirectory = Path.Combine(Path.GetTempPath(), "signal-forge-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(storeDirectory);
try
{
    var storePath = Path.Combine(storeDirectory, "profiles.json");
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["DdsProfiles:StoragePath"] = storePath,
    }).Build();
    var profileService = new DdsProfileService(new FakeEnvironment(repositoryRoot), configuration, sessions, NullLogger<DdsProfileService>.Instance);
    File.WriteAllText(storePath, JsonSerializer.Serialize(new
    {
        version = 1,
        revision = 4,
        profiles = new[]
        {
            new
            {
                id = Guid.NewGuid().ToString("N"),
                name = "legacy stored profile",
                typesXml = sampleTypes,
                configXml = legacyConfig,
                updatedAtUtc = DateTimeOffset.UtcNow,
            },
        },
    }));
    var catalog = await profileService.LoadAsync();
    var migratedJson = File.ReadAllText(storePath);
    Check(!migratedJson.Contains("\"ddsSimXml\"") && !migratedJson.Contains("\"topicsXml\"") &&
          !migratedJson.Contains("\"qosProfilesXml\"") && !migratedJson.Contains("\"typesXml\"") &&
          !migratedJson.Contains("\"configXml\""),
          "profile manifest stores metadata only");
    var profileDirectory = Path.Combine(storeDirectory, "profiles", catalog.Profiles[0].Id, "versions", catalog.Profiles[0].StorageRevision!);
    Check(File.ReadAllText(Path.Combine(profileDirectory, "DDSSim.xml")) == catalog.Profiles[0].DdsSimXml,
          "DDSSim.xml persisted as a physical profile file");
    Check(File.Exists(Path.Combine(profileDirectory, "connection.json")) &&
          !File.Exists(Path.Combine(profileDirectory, "topics.xml")), "토픽 JSON 연결 설정 저장");
    Check(File.ReadAllText(Path.Combine(profileDirectory, "qos_profiles.xml")) == catalog.Profiles[0].QosProfilesXml,
          "qos_profiles.xml persisted as a physical profile file");
    var reloaded = await profileService.LoadAsync();
    Check(reloaded.Profiles[0].DdsSimXml == catalog.Profiles[0].DdsSimXml &&
          reloaded.Profiles[0].TopicSettingsJson == catalog.Profiles[0].TopicSettingsJson &&
          reloaded.Profiles[0].QosProfilesXml == catalog.Profiles[0].QosProfilesXml,
          "프로필 버전 다시 읽기");
    var ddsSimProfilePath = Path.Combine(profileDirectory, "DDSSim.xml");
    var unchangedFileTimestamp = DateTime.UtcNow.AddDays(-1);
    File.SetLastWriteTimeUtc(ddsSimProfilePath, unchangedFileTimestamp);
    catalog.Profiles[0].Name = "backup-test";
    catalog.Profiles[0].UpdatedAtUtc = DateTimeOffset.UtcNow;
    await profileService.SaveAsync(catalog);
    Check((File.GetLastWriteTimeUtc(ddsSimProfilePath) - unchangedFileTimestamp).Duration() < TimeSpan.FromSeconds(1),
          "metadata-only save skips unchanged XML files");
    File.WriteAllText(storePath, "{broken");
    var recovered = await profileService.LoadAsync();
    Check(recovered.Profiles.Count == 1, "profile backup recovery");
}
finally { Directory.Delete(storeDirectory, recursive: true); }

using (var isolatedState = new DdsStateService(sessions, NullLogger<DdsStateService>.Instance))
{
    await using var isolatedTriggers = new DdsTriggerService(
        isolatedState, sessions, NullLogger<DdsTriggerService>.Instance);
    isolatedTriggers.Add(new DdsTrigger { Id = "session-a-step", SessionId = "session-a", Name = "A", Enabled = false });
    isolatedTriggers.Add(new DdsTrigger { Id = "session-b-step", SessionId = "session-b", Name = "B", Enabled = false });
    isolatedTriggers.ReplaceSessionTriggers("session-a", []);
    Check(isolatedTriggers.Snapshot("session-a").Count == 0 &&
          isolatedTriggers.Snapshot("session-b").Single().Id == "session-b-step",
          "replacing one session automation preserves other sessions");
}

Console.WriteLine("Signal Forge smoke tests: PASS");

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAILED: {name}");
}

static void ExpectFailure(Action action, string name)
{
    try { action(); }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException($"FAILED: {name}");
}

sealed class FakeSessions : IDdsSessionService
{
    public event Action<string>? SessionDeleting;
    public void RaiseDeleting(string id) => SessionDeleting?.Invoke(id);
    public DdsSession Create(DdsSessionCreateRequest request) => throw new NotSupportedException();
    public DdsSession? Get(string sessionId) => new DdsSession { SessionId = sessionId, State = DdsSessionState.Active };
    public DdsParticipantHost? GetHost(string sessionId) => null;
    public IReadOnlyList<DdsSession> GetByProfileId(string profileId) => [];
    public Task DeleteAsync(string sessionId) => Task.CompletedTask;
    public IReadOnlyList<DdsSession> GetAll() => [];
}

sealed class FakeEnvironment : IWebHostEnvironment
{
    public FakeEnvironment(string root) { ContentRootPath = root; WebRootPath = Path.Combine(root, "wwwroot"); }
    public string ApplicationName { get; set; } = "SignalForge.SmokeTests";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; }
    public string EnvironmentName { get; set; } = "Tests";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
