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
Check(types["M::Ids"].AliasIsSequence && types["M::Ids"].AliasSequenceMaxLength == 8, "typedef collection metadata");
Check(types["M::Choice"].UnionCases.Single().Labels.Single() == "0", "union case metadata");
Check(DdsTypeProfileEditor.IsValidIdentifier("Messages_2"), "IDL module identifier accepted");
Check(!DdsTypeProfileEditor.IsValidIdentifier("2Messages"), "IDL module identifier start rejected");
Check(!DdsTypeProfileEditor.IsValidIdentifier("Company::Messages"), "IDL module input is one segment");

var editor = DdsTypeProfileEditor.Parse(xml);
DdsTypeProfileEditor.ValidateState(editor);
editor.Declarations.Single(item => item.Name == "Root").Members[0].TypeName = "M::Missing";
ExpectFailure(() => DdsTypeProfileEditor.ValidateState(editor), "missing type reference");

var topicsXml = """
<topics><topic custom="legacy" name="T" qos_profile="P" direction="Both"><custom>kept</custom></topic></topics>
""";
var qosProfilesXml = """
<dds><qos_library name="AmbassadorProfiles"><qos_profile name="P"/></qos_library></dds>
""";
var config = DdsConfigProfileEditor.Parse(topicsXml, qosProfilesXml);
var applied = DdsConfigProfileEditor.Apply(topicsXml, qosProfilesXml, config);
var topic = XDocument.Parse(applied.TopicsXml).Descendants("topic").Single();
Check(topic.Attribute("name")?.Value == "T" && topic.Attribute("custom") == null &&
      !topic.HasElements, "DDSClient topic normalization");
var parsedConfig = DdsConfigParser.Parse(applied.TopicsXml, applied.QosProfilesXml);
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

var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var sampleTypes = File.ReadAllText(Path.Combine(repositoryRoot, "samples", "dds", "DDSSim.xml"));
DdsTypeProfileEditor.ValidateState(DdsTypeProfileEditor.Parse(sampleTypes));
var qosXml = File.ReadAllText(Path.Combine(repositoryRoot, "samples", "dds", "qos_profiles.xml"));
var sampleTopics = File.ReadAllText(Path.Combine(repositoryRoot, "samples", "dds", "topics.xml"));
DdsProfileService.Validate(new DdsXmlProfile
{
    Name = "DDSClient contract",
    DdsSimXml = sampleTypes,
    TopicsXml = sampleTopics,
    QosProfilesXml = qosXml,
});
Check(DdsProfileFiles.RequiredFileNames.SequenceEqual(
          new[] { "DDSSim.xml", "topics.xml", "qos_profiles.xml" }),
      "DDSClient exact three file names");
var importedFiles = DdsProfileFiles.CreateProfile("imported contract", new Dictionary<string, string>
{
    ["DDSSim.xml"] = sampleTypes,
    ["topics.xml"] = sampleTopics,
    ["qos_profiles.xml"] = qosXml,
});
Check(importedFiles.DdsSimXml == sampleTypes && importedFiles.TopicsXml == sampleTopics &&
      importedFiles.QosProfilesXml == qosXml,
      "three XML file import");
ExpectFailure(
    () => DdsProfileFiles.CreateProfile("missing file", new Dictionary<string, string>
    {
        ["DDSSim.xml"] = sampleTypes,
        ["topics.xml"] = sampleTopics,
    }),
    "all three DDSClient files are required");
var sampleTopicsDocument = XDocument.Parse(sampleTopics);
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
Check(legacyProfile.DdsSimXml.Length > 0 && legacyProfile.TopicsXml.Length > 0 &&
      legacyProfile.QosProfilesXml.Length > 0 && legacyProfile.LegacyConfigXml == null,
      "legacy profile migrated to three files");
using var loggerFactory = LoggerFactory.Create(_ => { });
var host = new DdsParticipantHostFactory(loggerFactory).Create(
    new DdsTransportSettings { DomainId = 0 }, sampleTypes, sampleTopics, qosXml);
host.ValidateQosProfile("AmbassadorProfiles::ReliableRealtime");
await host.DisposeAsync();

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
    var profileDirectory = Path.Combine(storeDirectory, "profiles", catalog.Profiles[0].Id);
    Check(File.ReadAllText(Path.Combine(profileDirectory, "DDSSim.xml")) == catalog.Profiles[0].DdsSimXml,
          "DDSSim.xml persisted as a physical profile file");
    Check(File.ReadAllText(Path.Combine(profileDirectory, "topics.xml")) == catalog.Profiles[0].TopicsXml,
          "topics.xml persisted as a physical profile file");
    Check(File.ReadAllText(Path.Combine(profileDirectory, "qos_profiles.xml")) == catalog.Profiles[0].QosProfilesXml,
          "qos_profiles.xml persisted as a physical profile file");
    var reloaded = await profileService.LoadAsync();
    Check(reloaded.Profiles[0].DdsSimXml == catalog.Profiles[0].DdsSimXml &&
          reloaded.Profiles[0].TopicsXml == catalog.Profiles[0].TopicsXml &&
          reloaded.Profiles[0].QosProfilesXml == catalog.Profiles[0].QosProfilesXml,
          "metadata manifest reloads the three physical files");
    catalog.Profiles[0].Name = "backup-test";
    catalog.Profiles[0].UpdatedAtUtc = DateTimeOffset.UtcNow;
    await profileService.SaveAsync(catalog);
    File.WriteAllText(storePath, "{broken");
    var recovered = await profileService.LoadAsync();
    Check(recovered.Profiles.Count == 1, "profile backup recovery");
}
finally { Directory.Delete(storeDirectory, recursive: true); }

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
    public DdsSession? Get(string sessionId) => null;
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
