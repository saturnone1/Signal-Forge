using DdsAmbassador.Messages;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using ASAP.Dds;
using ASAP.Models.Dds;
using ASAP.Services;
using DdsAmbassador.DDSClient;
using DdsModules_DDSSim.MSG;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Contains("--settings-regression")) { await SettingsRegressionProbe.Run(); return; }
if (args.Contains("--module-rti-probe")) { await SettingsRegressionProbe.RunRti(); return; }
if (args.Contains("--module-catalog")) { Console.WriteLine(string.Join(",", BuiltMessageRegistry.Modules.Select(module => module.Module.Name + ":" + module.Module.Version))); return; }
if (args.Contains("--adapter-regression"))
{
    var passed = 0;
    await AdapterRegressionTests.Run((ok, name) => { if (!ok) throw new InvalidOperationException(name); passed++; Console.WriteLine("통과: " + name); });
    Console.WriteLine($"어댑터 회귀 검사 {passed}개 통과");
    return;
}
if (args.Contains("--dds-only-performance")) { await RuntimeStressProbe.RunDdsOnly(); return; }
if (args.Contains("--runtime-stress")) { await RuntimeStressProbe.Run(); return; }
if (args.Contains("--benchmark")) { PerformanceProbe.Run(); return; }
var ddssim = BuiltMessageRegistry.Get("DDSSim");
var telemetry = BuiltMessageRegistry.Get("Telemetry");
var contract = ddssim.ContractXml;
if (ddssim.Module.SchemaSha256 is not { Length: 64 }) throw new Exception("Missing compiled schema fingerprint");
const string topics = """
<topics><topic name="TimeTickInformation" qos_profile="P" direction="Both"/></topics>
""";
// Loopback-only UDP; no shared memory or LAN traffic, separate from the production domain.
const string qos = """
<dds><qos_library name="AmbassadorProfiles"><qos_profile name="P" is_default_qos="true">
<participant_qos><transport_builtin><mask>UDPv4</mask></transport_builtin>
<discovery><initial_peers><element>udpv4://127.0.0.1</element></initial_peers><multicast_receive_addresses/></discovery>
<property><value><element><name>dds.transport.UDPv4.builtin.parent.allow_interfaces</name><value>127.0.0.1</value></element></value></property>
</participant_qos>
<datawriter_qos><reliability><kind>RELIABLE_RELIABILITY_QOS</kind></reliability><history><kind>KEEP_ALL_HISTORY_QOS</kind></history></datawriter_qos>
<datareader_qos><reliability><kind>RELIABLE_RELIABILITY_QOS</kind></reliability><history><kind>KEEP_ALL_HISTORY_QOS</kind></history></datareader_qos>
</qos_profile></qos_library></dds>
""";
var tests = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    tests++;
    Console.WriteLine("PASS: " + name);
}
void Reject(Action action, string expected, string name)
{
    try { action(); }
    catch (Exception e) when (e.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)) { Check(true, name); return; }
    throw new InvalidOperationException("FAIL (expected rejection): " + name);
}

var registry = BuiltMessageRegistry.ValidateAndBind(contract, topics, qos);
Check(registry["TimeTickInformation"].Definition.SampleType == ddssim.ResolveType("MSG::TimeTickInformation"), "explicit built type binding");
var formatted = XDocument.Parse(contract);
formatted.Root!.AddFirst(new XComment("formatting only"));
BuiltMessageRegistry.ValidateContract(formatted.ToString(SaveOptions.DisableFormatting));
Check(true, "formatting and comments do not change contract");
var changed = XDocument.Parse(contract);
changed.Descendants("struct").First(e => (string?)e.Attribute("name") == "TimeTickInformation")
    .Add(new XElement("member", new XAttribute("name", "UnknownField"), new XAttribute("type", "int32")));
var factory = new DdsParticipantHostFactory(NullLoggerFactory.Instance);
Reject(() => factory.Create(new(), changed.ToString(), topics, qos), "메시지 XML이 선택한 빌드 모듈과 다릅니다", "same name wrong shape rejected before participant creation");
var unknown = contract.Replace("name=\"TimeTickInformation\"", "name=\"UnbuiltMessage\"", StringComparison.Ordinal);
Reject(() => factory.Create(new(), unknown, topics, qos), "메시지 XML이 선택한 빌드 모듈과 다릅니다", "unknown type rejected before participant creation");
Reject(() => BuiltMessageRegistry.ValidateAndBind(contract, topics.Replace("TimeTickInformation", "Unknown"), qos), "Unsupported XML", "unknown topic rejected");
Reject(() => BuiltMessageRegistry.ValidateAndBind(contract, topics.Replace("Both", "both"), qos), "송수신 방향", "directions are case-sensitive");
Reject(() => ddssim.CreateSample("MSG::timetickinformation"), "Unsupported XML", "DDS type names are case-sensitive");
Check(BuiltMessageRegistry.Select(telemetry.ContractXml) == telemetry, "Telemetry selected by complete uploaded contract");
Reject(() => BuiltMessageRegistry.Select(telemetry.ContractXml, "Unknown"), "Unsupported built", "unknown explicit module rejected");
Reject(() => BuiltMessageRegistry.Select(telemetry.ContractXml, "DDSSim"), "메시지 XML이 선택한 빌드 모듈과 다릅니다", "explicit module must match uploaded shape");
Reject(() => BuiltMessageRegistry.Select(telemetry.ContractXml.Replace("uint64", "uint32")), "메시지 XML이 선택한 빌드 모듈과 다릅니다", "Telemetry same-name wrong-shape rejected");
Reject(() => ddssim.CreateSample("Telemetry::Heartbeat"), "Unsupported XML", "module does not see another module's types");
var telemetryTopics = """<topics><topic name="Shared" type_name="Telemetry::Heartbeat" qos_profile="P" direction="Both"/></topics>""";
var telemetryBinding = BuiltMessageRegistry.ValidateAndBind(telemetry.ContractXml, telemetryTopics, qos);
Check(telemetryBinding["Shared"].TypeName == "Telemetry::Heartbeat", "wire type strips only generated module prefix");
var editor = DdsConfigProfileEditor.Parse(telemetryTopics, qos);
var edited = DdsConfigProfileEditor.Apply(telemetryTopics, qos, editor);
Check(DdsConfigParser.Parse(edited.TopicSettingsJson, edited.QosProfilesXml, telemetry).Topics.Single().TypeName == "Telemetry::Heartbeat", "topic aliases survive structured editing");
DdsProfileService.Validate(new DdsXmlProfile { Name = "Telemetry", DdsSimXml = telemetry.ContractXml, TopicSettingsJson = telemetryTopics, QosProfilesXml = qos });
Check(true, "non-MSG module profile validation");
foreach (var module in BuiltMessageRegistry.Modules)
foreach (var (name, _) in module.Types)
{
    using var sample = module.CreateSample(name);
    var json = DdsJsonConverter.ToJson(sample);
    DdsJsonConverter.ApplyJson(sample, json);
    Check(JsonElement.DeepEquals(JsonDocument.Parse(json).RootElement, JsonDocument.Parse(DdsJsonConverter.ToJson(sample)).RootElement), name + " default JSON roundtrip");
}
using (var sample = new DdsTypedSample(new AirThreatInformation()))
{
    DdsJsonConverter.ApplyJson(sample, """{"AirCount":1,"AirObjects":[{}],"Header":{"TimeTick":18446744073709551615,"SrcSimID":"TCC"}}""");
    var typed = (AirThreatInformation)sample.Value;
    Check(typed.AirObjects.Count == 1 && typed.Header.TimeTick == ulong.MaxValue, "read-only nested sequence populated and uint64 preserved");
    var json = DdsJsonConverter.ToJson(sample);
    DdsJsonConverter.ApplyJson(sample, json);
    Check(((AirThreatInformation)sample.Value).AirObjects.Count == 1, "reapplying JSON does not append stale sequence items");
    Reject(() => DdsJsonConverter.ApplyJson(sample, """{"AirCount":70000}"""), "convert", "numeric overflow rejected");
    Reject(() => DdsJsonConverter.ApplyJson(sample, """{"Extra":1}"""), "Extra", "unknown JSON fields rejected");
    Reject(() => DdsJsonConverter.ApplyJson(sample, """{"Header":null}"""), "null", "null struct rejected");
    Reject(() => DdsJsonConverter.ApplyJson(sample, """{"AirObjects":null}"""), "null", "null get-only sequence rejected");
    Reject(() => DdsJsonConverter.ApplyJson(sample, """{"AirObjects":[null]}"""), "null", "null nested sequence element rejected");
    Reject(() => DdsJsonConverter.ApplyJson(sample, "null"), "null", "null root rejected");
    Reject(() => DdsJsonConverter.ApplyJson(sample, "[]"), "object", "array root rejected");
    Reject(() => DdsJsonConverter.ApplyJson(sample, """{"AirCount":1,"AirCount":2}"""), "중복 DDS 멤버", "duplicate JSON members rejected");
    Check(((AirThreatInformation)sample.Value).AirObjects.Count == 1, "invalid JSON leaves previous sample intact");
}
using (var sample = new DdsTypedSample(new UplinkInformation()))
{
    DdsJsonConverter.ApplyJson(sample, """{"UplinkCount":1,"UplinkMessages":[{}]}""");
    Check(((UplinkInformation)sample.Value).UplinkMessages.Single().Data.Length == 26, "fixed arrays inside sequences preserve defaults");
    Reject(() => DdsJsonConverter.ApplyJson(sample, """{"UplinkMessages":[{"Data":[1]}]}"""), "26", "wrong fixed array length rejected");
    Reject(() => DdsJsonConverter.ApplyJson(sample, """{"UplinkMessages":[{"Data":null}]}"""), "null", "null fixed array rejected");
    Reject(() => DdsJsonConverter.ApplyJson(sample, """{"UplinkMessages":[{"Data":[]}]}"""), "26", "empty fixed array rejected");
}
using (var sample = new DdsTypedSample(new SetLog()))
{
    DdsJsonConverter.ApplyJson(sample, """{"LogSaveNetworkList":["DDS","RTI"]}""");
    Check(((SetLog)sample.Value).LogSaveNetworkList.Count == 2, "enum sequence populated");
    Reject(() => DdsJsonConverter.ApplyJson(sample, "{\"LogSaveNetworkList\":[" + string.Join(",", Enumerable.Repeat("\"DDS\"", 21)) + "]}"), "20", "bounded sequence length enforced");
}
using (var sample = ddssim.CreateSample("MSG::SetScenario"))
    Reject(() => DdsJsonConverter.ApplyJson(sample, JsonSerializer.Serialize(new { ScenarioName = new string('x', 256) })), "255", "bounded string length enforced");
var effective = DdsSessionQos.Create(qos, new DdsTransportSettings { DomainId = 191, SendBufferSize = 65536, DiscoveryMode = DdsDiscoveryMode.Unicast });
var effectiveDocument = XDocument.Parse(effective.Xml);
var participant = effectiveDocument.Descendants("qos_profile").Last().Element("participant_qos")!;
Check(participant.Element("discovery")!.Element("multicast_receive_addresses")!.IsEmpty, "unicast discovery clears multicast");
Check(participant.Descendants("value").Any(v => v.Value == "127.0.0.1"), "inherited interface properties preserved");
Check(effective.ParticipantProfile != DdsSessionQos.Create(qos, new()).ParticipantProfile, "participant QoS is session-local");
Check(XNode.DeepEquals(XDocument.Parse(qos).Root!.Elements().Single().Elements().Single(),
    effectiveDocument.Root!.Elements().Single().Elements().First()), "original writer/reader QoS remains unchanged");
Reject(() => DdsSessionQos.Create(qos, new() { DiscoveryMode = DdsDiscoveryMode.Multicast, MulticastAddress = "127.0.0.1" }), "multicast", "invalid discovery address rejected");

using (var sourceStream = typeof(MultiNetworkTests).Assembly.GetManifestResourceStream("Tests.DDSClient.qos_profiles.xml")!)
using (var sourceReader = new StreamReader(sourceStream))
{
    var uploaded = XDocument.Parse(sourceReader.ReadToEnd());
    var sourceProfile = uploaded.Descendants("qos_profile").First(p => (string?)p.Attribute("name") == "ReliableRealtime");
    sourceProfile.SetAttributeValue("is_default_qos", "true");
    var sourceParticipant = sourceProfile.Element("domain_participant_qos")!;
    Check(sourceParticipant is not null, "fixture uses DDSClient domain_participant_qos spelling");
    var sourceDiscovery = sourceParticipant!.Element("discovery")!;
    var originalPeers = sourceDiscovery.Element("initial_peers")!;
    originalPeers.Add(new XElement("element", "udpv4://192.0.2.25"));
    var propertyPolicy = sourceParticipant.Element("property");
    if (propertyPolicy is null) { propertyPolicy = new XElement("property", new XElement("value")); sourceParticipant.Add(propertyPolicy); }
    propertyPolicy.Element("value")!.Add(new XElement("element", new XElement("name", "signalforge.test.property"), new XElement("value", "preserved")));
    var translated = XDocument.Parse(DdsSessionQos.Create(uploaded.ToString(), new() { AllowInterfaces = ["127.0.0.1"] }).Xml);
    var derived = translated.Descendants("qos_profile").Last().Element("participant_qos")!;
    Check((string?)derived.Parent!.Attribute("base_name") == "AmbassadorProfiles::ReliableRealtime" &&
        translated.Descendants("qos_profile").First().Descendants("initial_peers").Elements("element").Any(e => e.Value == "udpv4://192.0.2.25"), "DDSClient uploaded QoS inherits custom peer");
    Check(derived.Descendants("element").Any(e => (string?)e.Element("name") == "signalforge.test.property" && (string?)e.Element("value") == "preserved"), "DDSClient uploaded QoS retains custom property with NIC override");
    sourceProfile.Add(new XElement("participant_qos"));
    Reject(() => DdsSessionQos.Create(uploaded.ToString(), new()), "conflicting", "duplicate participant policy spellings rejected");
}

await AdapterRegressionTests.Run(Check);

if (args.Contains("--rti", StringComparer.Ordinal))
{
    await MultiNetworkTests.Run(qos, Check);
    var directory = Path.Combine(Path.GetTempPath(), "signal-forge-adapter-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var qosPath = Path.Combine(directory, "qos_profiles.xml");
        File.WriteAllText(qosPath, qos);
        DdsClientOptions Options(int domain) => new()
        {
            MessageModules = [DDSSimMessages.Module], DomainId = domain,
            ParticipantQosProfile = "AmbassadorProfiles::P", QosProfilesXmlPath = qosPath,
            Topics = [Topic.Both<TimeTickInformation>("P")],
        };
        await using var sessions = new DdsSessionService(factory, NullLogger<DdsSessionService>.Instance);
        using var state = new DdsStateService(sessions, NullLogger<DdsStateService>.Instance);
        DdsSessionCreateRequest Request(int domain) => new()
        {
            Name = "adapter-test", ProfileId = "test", ProfileName = "test", ProfileUpdatedAtUtc = DateTimeOffset.UtcNow,
            Transport = new() { DomainId = domain, AllowInterfaces = ["127.0.0.1"] },
            DdsSimXmlContent = contract, TopicSettingsJsonContent = topics, QosProfilesXmlContent = qos,
        };
        var a = sessions.Create(Request(IntegrationDomains.Resolve(191)));
        var b = sessions.Create(Request(IntegrationDomains.Resolve(192)));
        var subscriptions = Enumerable.Range(0, 3).Select(_ => state.StartSubscription(a.SessionId, "TimeTickInformation", "MSG::TimeTickInformation", "P")).ToArray();
        var isolated = state.StartSubscription(b.SessionId, "TimeTickInformation", "MSG::TimeTickInformation", "P");
        using var peer = DdsClient.Connect(Options(IntegrationDomains.Resolve(191)));
        var publisher = peer.CreatePublisher<TimeTickInformation>();
        publisher.WaitForSubscribers(3, TimeSpan.FromSeconds(15));
        for (ulong i = 1; i <= 4; i++)
        {
            var sample = new TimeTickInformation();
            sample.Header.TimeTick = i;
            publisher.Publish(sample);
        }
        publisher.WaitForAcknowledgments(TimeSpan.FromSeconds(10));
        await Until(() => subscriptions.All(s => Interlocked.Read(ref s.ReceivedCount) == 4));
        Check(subscriptions.All(s => s.ReceivedCount == 4), "typed peer -> three adapter subscriptions receive all four samples");
        Check(isolated.ReceivedCount == 0, "same message type on another domain remains isolated");
        Check(subscriptions.All(s => s.LastSample!.ReceiveLatencyMs is null && s.LastSample.SourceTimestampNs == 0), "source timestamp unavailable is explicit");

        var retiredSubscription = subscriptions[0];
        subscriptions[0] = state.RestartSubscription(retiredSubscription.SubscriptionId);
        publisher.WaitForSubscribers(3, TimeSpan.FromSeconds(15));
        publisher.Publish(new TimeTickInformation());
        publisher.WaitForAcknowledgments(TimeSpan.FromSeconds(10));
        await Until(() => subscriptions[0].ReceivedCount == 1 && subscriptions.Skip(1).All(s => s.ReceivedCount == 5));
        Check(!retiredSubscription.IsActive && subscriptions[0].SubscriptionId != retiredSubscription.SubscriptionId &&
            state.SnapshotSubscriptions(a.SessionId).Count == 3, "restart replaces one reader without duplicating it or interrupting same-topic peers");

        var response = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var peerSubscription = peer.Subscribe<TimeTickInformation>(message => { if (message.Header.TimeTick == 77) response.TrySetResult(77); });
        sessions.GetHost(a.SessionId)!.WaitForSubscribers("TimeTickInformation", "MSG::TimeTickInformation", "P", 4, TimeSpan.FromSeconds(15));
        Check(state.Publish(a.SessionId, "TimeTickInformation", "MSG::TimeTickInformation", "P", """{"Header":{"TimeTick":77}}""").Success, "adapter publish accepted");
        Check(await response.Task.WaitAsync(TimeSpan.FromSeconds(10)) == 77, "adapter JSON -> typed peer payload preserved");
        Check(!state.Publish(a.SessionId, "TimeTickInformation", "MSG::TimeTickInformation", "Wrong", "{}").Success, "QoS cannot silently change mid-session");
        foreach (var subscription in subscriptions) state.StopSubscription(subscription.SubscriptionId);
        Action<string> failingObserver = _ => throw new InvalidOperationException("Injected RTI deletion observer failure");
        sessions.SessionDeleting += failingObserver;
        await sessions.DeleteAsync(a.SessionId);
        sessions.SessionDeleting -= failingObserver;
        Check(sessions.GetHost(a.SessionId) is null && sessions.Get(a.SessionId) is null,
            "RTI host and session removed even when deletion observer throws");
        using var peerB = DdsClient.Connect(Options(IntegrationDomains.Resolve(192)));
        var writerB = peerB.CreatePublisher<TimeTickInformation>();
        writerB.WaitForSubscribers(1, TimeSpan.FromSeconds(15));
        writerB.Publish(new TimeTickInformation());
        await Until(() => isolated.ReceivedCount == 1);
        Check(isolated.ReceivedCount == 1, "closing one session leaves another participant alive");
        var restarted = sessions.Create(Request(IntegrationDomains.Resolve(191)));
        var restartSubscription = state.StartSubscription(restarted.SessionId, "TimeTickInformation", "MSG::TimeTickInformation", "P");
        publisher.WaitForSubscribers(2, TimeSpan.FromSeconds(15)); // peer's own reader plus restarted adapter
        publisher.Publish(new TimeTickInformation());
        await Until(() => restartSubscription.ReceivedCount == 1);
        Check(restartSubscription.ReceivedCount == 1, "closed session can reconnect and receive");
        var publishOnly = Request(IntegrationDomains.Resolve(193));
        await using var restricted = factory.Create(publishOnly.Transport, contract, topics.Replace("Both", "Publish"), qos);
        Reject(() => restricted.Subscribe("TimeTickInformation", "MSG::TimeTickInformation", "P", _ => { }), "구독할 수 없습니다", "publish-only direction enforced by public core API");

        // Keep this regression below Windows' dynamic UDP exclusion range.
        await using var readiness = factory.Create(new DdsTransportSettings { DomainId = 108, DiscoveryTimeoutMs = 50 }, contract, topics, qos);
        var temporaryReader = readiness.Subscribe("TimeTickInformation", "MSG::TimeTickInformation", "P", _ => { });
        readiness.WaitForSubscribers("TimeTickInformation", "MSG::TimeTickInformation", "P", 1, TimeSpan.FromSeconds(10));
        temporaryReader.Dispose();
        await Until(() =>
        {
            try { readiness.WaitForSubscribers("TimeTickInformation", "MSG::TimeTickInformation", "P", 1, TimeSpan.Zero); return false; }
            catch (TimeoutException) { return true; }
        });
        // 구독자 이탈 이후에도 Writer 준비는 허용한다. 준비는 연결 대기와 별개다.
        readiness.PreparePublisher("TimeTickInformation", "MSG::TimeTickInformation", "P");
        Check(true, "writer preparation does not require a matched reader");
        using var replacementReader = readiness.Subscribe("TimeTickInformation", "MSG::TimeTickInformation", "P", _ => { });
        readiness.WaitForSubscribers("TimeTickInformation", "MSG::TimeTickInformation", "P", 1, TimeSpan.FromSeconds(10));
        readiness.PreparePublisher("TimeTickInformation", "MSG::TimeTickInformation", "P");
        Check(true, "writer becomes ready again after reader reconnects");
    }
    finally { Directory.Delete(directory, recursive: true); }
}
else Console.WriteLine("RTI integration tests not requested; pass --rti to run loopback domains 186 and 191–194.");
Console.WriteLine($"PASS: {tests} integration checks");

static async Task Until(Func<bool> condition)
{
    var limit = DateTime.UtcNow.AddSeconds(15);
    while (!condition())
    {
        if (DateTime.UtcNow >= limit) throw new TimeoutException("DDS delivery did not complete within 15 seconds.");
        await Task.Delay(20);
    }
}
