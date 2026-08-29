using System.Xml.Linq;
using ASAP.Dds;
using ASAP.Models.Dds;

namespace ASAP.Services;

public sealed class DdsConfigEditorState
{
    public string QosLibraryName { get; set; } = DdsConfigParser.RequiredQosLibraryName;
    public List<DdsTopicEditorItem> Topics { get; set; } = [];
    public List<DdsQosEditorItem> QosProfiles { get; set; } = [];
}

public sealed class DdsTopicEditorItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string TypeName
    {
        get => string.IsNullOrWhiteSpace(Name) ? string.Empty : $"MSG::{Name}";
        set
        {
            if (value.StartsWith("MSG::", StringComparison.Ordinal))
                Name = value["MSG::".Length..];
        }
    }
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

public sealed record DdsConfigXmlFiles(string TopicsXml, string QosProfilesXml);

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

    public static DdsConfigEditorState Parse(string topicsXml, string qosProfilesXml)
    {
        var topicsDocument = XDocument.Parse(topicsXml);
        if (topicsDocument.Root?.Name.LocalName != "topics")
            throw new InvalidOperationException("topics.xml root는 <topics>여야 합니다.");

        var qosDocument = XDocument.Parse(qosProfilesXml);
        if (qosDocument.Root?.Name.LocalName != "dds")
            throw new InvalidOperationException("qos_profiles.xml root는 <dds>여야 합니다.");
        var library = qosDocument.Root.DescendantsAndSelf()
            .FirstOrDefault(element =>
                element.Name.LocalName == "qos_library" &&
                string.Equals(element.Attribute("name")?.Value, DdsConfigParser.RequiredQosLibraryName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"qos_profiles.xml에 <qos_library name=\"{DdsConfigParser.RequiredQosLibraryName}\">가 필요합니다.");

        var state = new DdsConfigEditorState();
        foreach (var topic in topicsDocument.Root.Elements().Where(element => element.Name.LocalName == "topic"))
        {
            var name = topic.Attribute("name")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            state.Topics.Add(new DdsTopicEditorItem
            {
                Name = name,
                Direction = Enum.TryParse<DdsTopicDirection>(topic.Attribute("direction")?.Value, false, out var direction)
                    ? direction
                    : DdsTopicDirection.Both,
                QosProfileName = topic.Attribute("qos_profile")?.Value?.Trim() ?? string.Empty,
                SourceXml = topic.ToString(SaveOptions.DisableFormatting),
            });
        }

        foreach (var profile in library.Elements().Where(element => element.Name.LocalName == "qos_profile"))
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
        return state;
    }

    public static DdsConfigXmlFiles Apply(
        string originalTopicsXml,
        string originalQosProfilesXml,
        DdsConfigEditorState state)
    {
        ValidateState(state);

        var topicsDocument = XDocument.Parse(originalTopicsXml);
        var topicsRoot = topicsDocument.Root?.Name.LocalName == "topics"
            ? topicsDocument.Root
            : throw new InvalidOperationException("topics.xml root는 <topics>여야 합니다.");
        topicsRoot.Elements().Where(element => element.Name.LocalName == "topic").Remove();
        foreach (var draft in state.Topics)
        {
            XElement topic;
            try { topic = string.IsNullOrWhiteSpace(draft.SourceXml) ? new XElement("topic") : XElement.Parse(draft.SourceXml); }
            catch { topic = new XElement("topic"); }
            topic.RemoveAttributes();
            topic.RemoveNodes();
            topic.SetAttributeValue("name", draft.Name.Trim());
            topic.SetAttributeValue("qos_profile", draft.QosProfileName.Trim());
            topic.SetAttributeValue("direction", draft.Direction.ToString());
            topicsRoot.Add(topic);
        }

        var qosDocument = XDocument.Parse(originalQosProfilesXml);
        if (qosDocument.Root?.Name.LocalName != "dds")
            throw new InvalidOperationException("qos_profiles.xml root는 <dds>여야 합니다.");
        var library = qosDocument.Root.DescendantsAndSelf()
            .FirstOrDefault(element => element.Name.LocalName == "qos_library")
            ?? throw new InvalidOperationException("qos_profiles.xml에 qos_library가 없습니다.");
        library.SetAttributeValue("name", DdsConfigParser.RequiredQosLibraryName);

        var existingProfiles = library.Elements()
            .Where(element => element.Name.LocalName == "qos_profile" &&
                              !string.IsNullOrWhiteSpace(element.Attribute("name")?.Value))
            .ToDictionary(element => element.Attribute("name")!.Value, StringComparer.OrdinalIgnoreCase);
        library.Elements().Where(element => element.Name.LocalName == "qos_profile").Remove();
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

        return new DdsConfigXmlFiles(topicsDocument.ToString(), qosDocument.ToString());
    }

    public static void ValidateState(DdsConfigEditorState state)
    {
        if (!string.Equals(state.QosLibraryName, DdsConfigParser.RequiredQosLibraryName, StringComparison.Ordinal))
            throw new InvalidOperationException($"QoS 라이브러리는 '{DdsConfigParser.RequiredQosLibraryName}'여야 합니다.");
        if (state.QosProfiles.Count == 0)
            throw new InvalidOperationException("QoS 프로필을 하나 이상 추가하세요.");
        if (state.Topics.Count == 0)
            throw new InvalidOperationException("토픽을 하나 이상 추가하세요.");

        var reliabilityKinds = new HashSet<string>([Reliable, BestEffort], StringComparer.Ordinal);
        var historyKinds = new HashSet<string>([KeepLast, KeepAll], StringComparer.Ordinal);
        var durabilityKinds = new HashSet<string>([Volatile, TransientLocal, Transient, Persistent], StringComparer.Ordinal);
        foreach (var profile in state.QosProfiles)
        {
            if (!reliabilityKinds.Contains(profile.WriterReliability) || !reliabilityKinds.Contains(profile.ReaderReliability) ||
                !historyKinds.Contains(profile.WriterHistory) || !historyKinds.Contains(profile.ReaderHistory) ||
                !durabilityKinds.Contains(profile.WriterDurability) || !durabilityKinds.Contains(profile.ReaderDurability))
                throw new InvalidOperationException($"QoS 프로필 '{profile.Name}'에 지원하지 않는 정책 값이 있습니다.");
            if (profile.WriterHistory == KeepLast && profile.WriterHistoryDepth < 1 ||
                profile.ReaderHistory == KeepLast && profile.ReaderHistoryDepth < 1)
                throw new InvalidOperationException($"QoS 프로필 '{profile.Name}'의 KEEP_LAST depth는 1 이상이어야 합니다.");
        }

        var duplicateQos = state.QosProfiles
            .GroupBy(profile => profile.Name.Trim(), StringComparer.Ordinal)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateQos != null)
            throw new InvalidOperationException("QoS 프로필 이름은 비어 있거나 중복될 수 없습니다.");
        var qosNames = state.QosProfiles.Select(profile => profile.Name.Trim()).ToHashSet(StringComparer.Ordinal);

        var duplicateTopic = state.Topics
            .GroupBy(topic => topic.Name.Trim(), StringComparer.Ordinal)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateTopic != null)
            throw new InvalidOperationException("토픽 이름은 비어 있거나 중복될 수 없습니다.");
        foreach (var topic in state.Topics)
        {
            if (!DdsTypeProfileEditor.IsValidIdentifier(topic.Name))
                throw new InvalidOperationException($"토픽 '{topic.Name}'은 올바른 MSG struct 이름이 아닙니다.");
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
            (depthElement ?? EnsureChild(history, "depth")).Value = depth.ToString();
        else
            depthElement?.Remove();
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
}
