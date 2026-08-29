using System.Xml.Linq;
using ASAP.Models.Dds;

namespace ASAP.Services;

public sealed class DdsConfigEditorState
{
    public string QosLibraryName { get; set; } = "SignalForgeProfiles";
    public List<DdsTopicEditorItem> Topics { get; set; } = [];
    public List<DdsQosEditorItem> QosProfiles { get; set; } = [];
}

public sealed class DdsTopicEditorItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Topic";
    public string TypeName { get; set; } = string.Empty;
    public DdsTopicDirection Direction { get; set; } = DdsTopicDirection.Both;
    public string QosProfileName { get; set; } = string.Empty;
    public string? SourceXml { get; set; }
}

public sealed class DdsQosEditorItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? OriginalName { get; set; }
    public string Name { get; set; } = "QosProfile";
    public string WriterReliability { get; set; } = DdsConfigProfileEditor.Reliable;
    public string ReaderReliability { get; set; } = DdsConfigProfileEditor.Reliable;
    public string WriterHistory { get; set; } = DdsConfigProfileEditor.KeepLast;
    public string ReaderHistory { get; set; } = DdsConfigProfileEditor.KeepLast;
    public int WriterHistoryDepth { get; set; } = 1;
    public int ReaderHistoryDepth { get; set; } = 1;
    public string WriterDurability { get; set; } = DdsConfigProfileEditor.Volatile;
    public string ReaderDurability { get; set; } = DdsConfigProfileEditor.Volatile;
}

public static class DdsConfigProfileEditor
{
    public const string Reliable = "RELIABLE_RELIABILITY_QOS";
    public const string BestEffort = "BEST_EFFORT_RELIABILITY_QOS";
    public const string KeepLast = "KEEP_LAST_HISTORY_QOS";
    public const string KeepAll = "KEEP_ALL_HISTORY_QOS";
    public const string Volatile = "VOLATILE_DURABILITY_QOS";
    public const string TransientLocal = "TRANSIENT_LOCAL_DURABILITY_QOS";
    public const string Transient = "TRANSIENT_DURABILITY_QOS";
    public const string Persistent = "PERSISTENT_DURABILITY_QOS";

    public static DdsConfigEditorState Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new InvalidOperationException("토픽/QoS XML root가 없습니다.");
        var dds = root.Name.LocalName == "dds" ? root : Child(root, "dds");
        var library = dds == null ? null : Child(dds, "qos_library");
        var topics = Child(root, "topics");

        var state = new DdsConfigEditorState
        {
            QosLibraryName = library?.Attribute("name")?.Value ?? "SignalForgeProfiles",
        };

        if (topics != null)
        {
            foreach (var topic in Children(topics, "topic"))
            {
                var name = Child(topic, "topic_name")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                state.Topics.Add(new DdsTopicEditorItem
                {
                    Name = name,
                    TypeName = Child(topic, "type_name")?.Value?.Trim() ?? string.Empty,
                    Direction = Enum.TryParse<DdsTopicDirection>(Child(topic, "direction")?.Value, true, out var direction)
                        ? direction
                        : DdsTopicDirection.Both,
                    QosProfileName = Child(topic, "qos_profile")?.Value?.Trim() ?? string.Empty,
                    SourceXml = topic.ToString(SaveOptions.DisableFormatting),
                });
            }
        }

        if (library != null)
        {
            foreach (var profile in Children(library, "qos_profile"))
            {
                var name = profile.Attribute("name")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                state.QosProfiles.Add(new DdsQosEditorItem
                {
                    OriginalName = name,
                    Name = name,
                    WriterReliability = PolicyValue(profile, "datawriter_qos", "reliability", "kind", Reliable),
                    ReaderReliability = PolicyValue(profile, "datareader_qos", "reliability", "kind", Reliable),
                    WriterHistory = PolicyValue(profile, "datawriter_qos", "history", "kind", KeepLast),
                    ReaderHistory = PolicyValue(profile, "datareader_qos", "history", "kind", KeepLast),
                    WriterHistoryDepth = PolicyInt(profile, "datawriter_qos", "history", "depth", 1),
                    ReaderHistoryDepth = PolicyInt(profile, "datareader_qos", "history", "depth", 1),
                    WriterDurability = PolicyValue(profile, "datawriter_qos", "durability", "kind", Volatile),
                    ReaderDurability = PolicyValue(profile, "datareader_qos", "durability", "kind", Volatile),
                });
            }
        }

        return state;
    }

    public static string Apply(string originalXml, DdsConfigEditorState state)
    {
        ValidateState(state);
        var document = XDocument.Parse(originalXml);
        var root = document.Root ?? throw new InvalidOperationException("토픽/QoS XML root가 없습니다.");
        var dds = root.Name.LocalName == "dds" ? root : EnsureChild(root, "dds");
        var library = EnsureChild(dds, "qos_library");
        library.SetAttributeValue("name", state.QosLibraryName.Trim());

        var existingProfiles = Children(library, "qos_profile")
            .Where(element => !string.IsNullOrWhiteSpace(element.Attribute("name")?.Value))
            .ToDictionary(element => element.Attribute("name")!.Value, StringComparer.OrdinalIgnoreCase);
        Children(library, "qos_profile").Remove();

        foreach (var draft in state.QosProfiles)
        {
            var profile = draft.OriginalName != null && existingProfiles.TryGetValue(draft.OriginalName, out var existing)
                ? new XElement(existing)
                : new XElement("qos_profile");
            profile.SetAttributeValue("name", draft.Name.Trim());
            SetPolicy(profile, "datawriter_qos", "reliability", "kind", draft.WriterReliability);
            SetPolicy(profile, "datareader_qos", "reliability", "kind", draft.ReaderReliability);
            SetHistory(profile, "datawriter_qos", draft.WriterHistory, draft.WriterHistoryDepth);
            SetHistory(profile, "datareader_qos", draft.ReaderHistory, draft.ReaderHistoryDepth);
            SetPolicy(profile, "datawriter_qos", "durability", "kind", draft.WriterDurability);
            SetPolicy(profile, "datareader_qos", "durability", "kind", draft.ReaderDurability);
            library.Add(profile);
        }

        if (root.Name.LocalName == "dds")
        {
            root = new XElement("dds-ambassador-config", new XElement(root), new XElement("topics"));
            document = new XDocument(document.Declaration, root);
        }

        var topics = EnsureChild(root, "topics");
        Children(topics, "topic").Remove();
        foreach (var draft in state.Topics)
        {
            XElement topic;
            try { topic = string.IsNullOrWhiteSpace(draft.SourceXml) ? new XElement("topic") : XElement.Parse(draft.SourceXml); }
            catch { topic = new XElement("topic"); }
            topic.Elements().Where(element => element.Name.LocalName is "topic_name" or "type_name" or "direction" or "qos_profile").Remove();
            topic.Add(new XElement("topic_name", draft.Name.Trim()), new XElement("type_name", draft.TypeName.Trim()),
                new XElement("direction", draft.Direction), new XElement("qos_profile", draft.QosProfileName.Trim()));
            topics.Add(topic);
        }

        return document.ToString();
    }

    public static void ValidateState(DdsConfigEditorState state)
    {
        if (string.IsNullOrWhiteSpace(state.QosLibraryName))
            throw new InvalidOperationException("QoS 라이브러리 이름을 입력하세요.");
        if (state.QosProfiles.Count == 0)
            throw new InvalidOperationException("QoS 프로필을 하나 이상 추가하세요.");
        if (state.Topics.Count == 0)
            throw new InvalidOperationException("토픽을 하나 이상 추가하세요.");

        try { System.Xml.XmlConvert.VerifyNCName(state.QosLibraryName.Trim()); }
        catch { throw new InvalidOperationException("QoS 라이브러리 이름은 올바른 XML/IDL 식별자여야 합니다."); }

        var reliabilityKinds = new HashSet<string>([Reliable, BestEffort], StringComparer.Ordinal);
        var historyKinds = new HashSet<string>([KeepLast, KeepAll], StringComparer.Ordinal);
        var durabilityKinds = new HashSet<string>([Volatile, TransientLocal, Transient, Persistent], StringComparer.Ordinal);
        foreach (var profile in state.QosProfiles)
        {
            if (!reliabilityKinds.Contains(profile.WriterReliability) || !reliabilityKinds.Contains(profile.ReaderReliability) ||
                !historyKinds.Contains(profile.WriterHistory) || !historyKinds.Contains(profile.ReaderHistory) ||
                !durabilityKinds.Contains(profile.WriterDurability) || !durabilityKinds.Contains(profile.ReaderDurability))
                throw new InvalidOperationException($"QoS 프로필 '{profile.Name}'에 지원하지 않는 정책 값이 있습니다.");
            if (profile.WriterHistory == KeepLast && profile.WriterHistoryDepth < 1 || profile.ReaderHistory == KeepLast && profile.ReaderHistoryDepth < 1)
                throw new InvalidOperationException($"QoS 프로필 '{profile.Name}'의 KEEP_LAST depth는 1 이상이어야 합니다.");
        }

        var duplicateQos = state.QosProfiles
            .GroupBy(profile => profile.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateQos != null)
            throw new InvalidOperationException("QoS 프로필 이름은 비어 있거나 중복될 수 없습니다.");

        var qosNames = state.QosProfiles.Select(profile => profile.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateTopic = state.Topics
            .GroupBy(topic => topic.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateTopic != null)
            throw new InvalidOperationException("토픽 이름은 비어 있거나 중복될 수 없습니다.");

        foreach (var topic in state.Topics)
        {
            if (string.IsNullOrWhiteSpace(topic.TypeName))
                throw new InvalidOperationException($"토픽 '{topic.Name}'의 타입을 선택하세요.");
            if (!qosNames.Contains(topic.QosProfileName))
                throw new InvalidOperationException($"토픽 '{topic.Name}'이 존재하지 않는 QoS '{topic.QosProfileName}'을 참조합니다.");
        }
    }

    private static void SetHistory(XElement profile, string endpointName, string kind, int depth)
    {
        SetPolicy(profile, endpointName, "history", "kind", kind);
        var history = EnsureChild(EnsureChild(profile, endpointName), "history");
        var depthElement = Child(history, "depth");
        if (kind == KeepLast)
        {
            (depthElement ?? EnsureChild(history, "depth")).Value = depth.ToString();
        }
        else
        {
            depthElement?.Remove();
        }
    }

    private static void SetPolicy(XElement profile, string endpointName, string policyName, string valueName, string value)
    {
        var endpoint = EnsureChild(profile, endpointName);
        var policy = EnsureChild(endpoint, policyName);
        EnsureChild(policy, valueName).Value = value;
    }

    private static string PolicyValue(XElement profile, string endpoint, string policy, string value, string fallback)
        => Child(Child(Child(profile, endpoint), policy), value)?.Value?.Trim() is { Length: > 0 } result ? result : fallback;

    private static int PolicyInt(XElement profile, string endpoint, string policy, string value, int fallback)
        => int.TryParse(Child(Child(Child(profile, endpoint), policy), value)?.Value, out var result) ? result : fallback;

    private static XElement EnsureChild(XElement parent, string localName)
    {
        var child = Child(parent, localName);
        if (child != null) return child;
        child = new XElement(localName);
        parent.Add(child);
        return child;
    }

    private static XElement? Child(XElement? parent, string localName)
        => parent?.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement parent, string localName)
        => parent.Elements().Where(element => element.Name.LocalName == localName);
}
