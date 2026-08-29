using System.Xml.Linq;
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

var configXml = """
<dds-ambassador-config><dds><qos_library name="Lib"><qos_profile name="P"/></qos_library></dds>
<topics><topic custom="keep"><custom>kept</custom><topic_name>T</topic_name><type_name>M::Root</type_name><direction>Both</direction><qos_profile>P</qos_profile></topic></topics></dds-ambassador-config>
""";
var config = DdsConfigProfileEditor.Parse(configXml);
var applied = DdsConfigProfileEditor.Apply(configXml, config);
var topic = XDocument.Parse(applied).Descendants("topic").Single();
Check(topic.Attribute("custom")?.Value == "keep" && topic.Element("custom")?.Value == "kept", "topic extension preservation");
config.QosProfiles[0].WriterHistoryDepth = 0;
ExpectFailure(() => DdsConfigProfileEditor.ValidateState(config), "invalid history depth");

var sessions = new FakeSessions();
using var state = new DdsStateService(sessions, NullLogger<DdsStateService>.Instance);
for (var i = 0; i < 80; i++) state.RecordExternalPublish("s", "t", "M::Root", new string('x', 300_000), true);
Check(state.SnapshotOutbound("s", 100).Count == 50, "bounded outbound history");
Check(state.SnapshotOutbound("s", 1)[0].JsonPayload.Length < 70_000, "bounded retained payload");
sessions.RaiseDeleting("s");
Check(state.SnapshotOutbound("s", 100).Count == 0, "session history cleanup");

var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var sampleTypes = File.ReadAllText(Path.Combine(repositoryRoot, "samples", "dds", "DDSSim.xml"));
DdsTypeProfileEditor.ValidateState(DdsTypeProfileEditor.Parse(sampleTypes));
var sampleQosLibrary = XElement.Load(Path.Combine(repositoryRoot, "samples", "dds", "qos_profiles.xml"));
var qosXml = new XDocument(new XElement("dds", sampleQosLibrary)).ToString();
using var loggerFactory = LoggerFactory.Create(_ => { });
var host = new DdsParticipantHostFactory(loggerFactory).Create(new DdsTransportSettings { DomainId = 0 }, sampleTypes, qosXml);
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
    var catalog = await profileService.LoadAsync();
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
