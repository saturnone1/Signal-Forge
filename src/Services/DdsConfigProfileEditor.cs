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
    public string? ExplicitTypeName { get; set; }
    public string TypeName
    {
        get => ExplicitTypeName ?? (string.IsNullOrWhiteSpace(Name) ? string.Empty : BuiltMessageRegistry.DefaultWireTypeName(Name));
        set => ExplicitTypeName = value;
    }
    public DdsTopicDirection Direction { get; set; } = DdsTopicDirection.Both;
    public string QosProfileName { get; set; } = string.Empty;
}

public sealed class DdsQosEditorItem
{
    // 최초 편집값과 비교하여 실제로 수정한 정책만 XML에 쓴다. 상속된 정책을 기본값으로 덮어쓰지 않는다.
    internal DdsQosEditorItem? OriginalPolicies { get; set; }
    public string? BaseName { get; set; }
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
    internal DdsQosEditorItem MemberwiseCloneForPolicies() => (DdsQosEditorItem)MemberwiseClone();
}

public sealed record DdsConfigXmlFiles(string TopicSettingsJson, string QosProfilesXml);

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

    public static DdsConfigEditorState Parse(string topicSettingsJson, string qosProfilesXml, string? qosLibraryName = null, BuiltMessageModule? module = null)
    {
        // 초안의 잘못된 연결도 표시하여 사용자가 고칠 수 있게 한다. 저장·연결 경로의 엄격 검증은 유지한다.
        var parsed = DdsConfigParser.Parse(topicSettingsJson, qosProfilesXml, module, qosLibraryName, validateBindings: false);
        var qosDocument = XDocument.Parse(qosProfilesXml);
        if (qosDocument.Root?.Name.LocalName != "dds")
            throw new InvalidOperationException("qos_profiles.xml root는 <dds>여야 합니다.");
        var library = DdsConfigParser.SelectLibrary(qosDocument, parsed.QosLibraryName);

        var state = new DdsConfigEditorState { QosLibraryName = parsed.QosLibraryName };
        state.Topics = parsed.Topics.Select(topic => new DdsTopicEditorItem
        {
            Name = topic.TopicName, ExplicitTypeName = topic.TypeName,
            Direction = topic.Direction, QosProfileName = topic.QosProfileName,
        }).ToList();

        foreach (var profile in library.Elements("qos_profile"))
        {
            var name = profile.Attribute("name")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var draft = new DdsQosEditorItem
            {
                OriginalName = name,
                BaseName = (string?)profile.Attribute("base_name"),
                Name = name,
                WriterReliability = PolicyValue(profile, "datawriter_qos", "reliability", "kind", Reliable),
                ReaderReliability = PolicyValue(profile, "datareader_qos", "reliability", "kind", Reliable),
                WriterHistory = PolicyValue(profile, "datawriter_qos", "history", "kind", KeepLast),
                ReaderHistory = PolicyValue(profile, "datareader_qos", "history", "kind", KeepLast),
                WriterHistoryDepth = PolicyInt(profile, "datawriter_qos", "history", "depth", 1),
                ReaderHistoryDepth = PolicyInt(profile, "datareader_qos", "history", "depth", 1),
                WriterDurability = PolicyValue(profile, "datawriter_qos", "durability", "kind", Volatile),
                ReaderDurability = PolicyValue(profile, "datareader_qos", "durability", "kind", Volatile),
            };
            draft.OriginalPolicies = draft.MemberwiseCloneForPolicies();
            state.QosProfiles.Add(draft);
        }
        return state;
    }

    public static DdsConfigXmlFiles Apply(
        string originalTopicSettingsJson,
        string originalQosProfilesXml,
        DdsConfigEditorState state)
    {
        ValidateState(state);

        var qosDocument = XDocument.Parse(originalQosProfilesXml);
        if (qosDocument.Root?.Name.LocalName != "dds")
            throw new InvalidOperationException("qos_profiles.xml root는 <dds>여야 합니다.");
        var library = DdsConfigParser.SelectLibrary(qosDocument, state.QosLibraryName);

        var existingProfiles = library.Elements("qos_profile")
            .Where(element => !string.IsNullOrWhiteSpace(element.Attribute("name")?.Value))
            .ToDictionary(element => element.Attribute("name")!.Value, StringComparer.Ordinal);
        library.Elements("qos_profile").Remove();
        foreach (var draft in state.QosProfiles)
        {
            var profile = draft.OriginalName != null && existingProfiles.TryGetValue(draft.OriginalName, out var existing)
                ? new XElement(existing)
                : new XElement("qos_profile");
            profile.SetAttributeValue("name", draft.Name.Trim());
            var original = draft.OriginalPolicies;
            if (original is null || draft.WriterReliability != original.WriterReliability)
                SetPolicy(profile, "datawriter_qos", "reliability", "kind", draft.WriterReliability);
            if (original is null || draft.ReaderReliability != original.ReaderReliability)
                SetPolicy(profile, "datareader_qos", "reliability", "kind", draft.ReaderReliability);
            if (original is null || draft.WriterHistory != original.WriterHistory || draft.WriterHistoryDepth != original.WriterHistoryDepth)
                SetHistory(profile, "datawriter_qos", draft.WriterHistory, draft.WriterHistoryDepth);
            if (original is null || draft.ReaderHistory != original.ReaderHistory || draft.ReaderHistoryDepth != original.ReaderHistoryDepth)
                SetHistory(profile, "datareader_qos", draft.ReaderHistory, draft.ReaderHistoryDepth);
            if (original is null || draft.WriterDurability != original.WriterDurability)
                SetPolicy(profile, "datawriter_qos", "durability", "kind", draft.WriterDurability);
            if (original is null || draft.ReaderDurability != original.ReaderDurability)
                SetPolicy(profile, "datareader_qos", "durability", "kind", draft.ReaderDurability);
            library.Add(profile);
        }

        var renamed = state.QosProfiles.Where(p => p.OriginalName is not null && p.OriginalName != p.Name.Trim())
            .ToDictionary(p => p.OriginalName!, p => p.Name.Trim(), StringComparer.Ordinal);
        var removed = existingProfiles.Keys.Except(state.QosProfiles.Select(p => p.OriginalName).OfType<string>(), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        foreach (var dependent in qosDocument.Descendants().Where(e => e.Name.LocalName == "qos_profile"))
        {
            if (dependent.Attribute("base_name") is not { } reference) continue;
            reference.Value = System.Text.RegularExpressions.Regex.Replace(reference.Value, @"[^,\s]+", match =>
            {
                var token = match.Value;
                var local = !token.Contains("::", StringComparison.Ordinal) && (string?)dependent.Parent?.Attribute("name") == state.QosLibraryName;
                var qualified = token.StartsWith(state.QosLibraryName + "::", StringComparison.Ordinal);
                if (!local && !qualified) return token;
                var name = local ? token : token[(state.QosLibraryName.Length + 2)..];
                if (removed.Contains(name)) throw new InvalidOperationException($"다른 QoS가 상속하는 프로파일 '{name}'은 삭제할 수 없습니다.");
                return renamed.TryGetValue(name, out var replacement) ? (local ? replacement : state.QosLibraryName + "::" + replacement) : token;
            });
        }

        return new DdsConfigXmlFiles(DdsTopicSettings.Write(state.Topics.Select(topic => new DdsTopicConfig
        {
            TopicName = topic.Name.Trim(), TypeName = topic.TypeName.Trim(),
            QosProfileName = topic.QosProfileName.Trim(), Direction = topic.Direction,
        })), qosDocument.ToString());
    }

    public static void ValidateState(DdsConfigEditorState state)
    {
        if (string.IsNullOrWhiteSpace(state.QosLibraryName))
            throw new InvalidOperationException("QoS 라이브러리 이름이 필요합니다.");
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
            if (string.IsNullOrWhiteSpace(topic.TypeName))
                throw new InvalidOperationException($"토픽 '{topic.Name}'의 메시지 타입을 선택하세요.");
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
        => ResolvePolicyValue(profile, endpoint, policy, value)?.Trim() is { Length: > 0 } result ? result : fallback;

    private static int PolicyInt(XElement profile, string endpoint, string policy, string value, int fallback)
        => int.TryParse(ResolvePolicyValue(profile, endpoint, policy, value), out var result) ? result : fallback;

    // 같은 XML에 있는 단일 상속 체인을 표시한다. RTI 내장·외부·다중 상속은 RTI에 맡기며 원문은 보존한다.
    private static string? ResolvePolicyValue(XElement profile, string endpoint, string policy, string value, HashSet<XElement>? visited = null)
    {
        visited ??= [];
        if (!visited.Add(profile)) throw new InvalidOperationException("QoS 프로파일 상속이 순환합니다.");
        var direct = Child(Child(Child(profile, endpoint), policy), value)?.Value;
        if (direct is not null) return direct;
        var reference = (string?)profile.Attribute("base_name");
        if (string.IsNullOrWhiteSpace(reference) || reference.Contains(',')) return null;
        var parts = reference.Split("::");
        var library = parts.Length == 2 ? parts[0] : (string?)profile.Parent?.Attribute("name");
        var parent = profile.Document?.Descendants().Where(e => e.Name.LocalName == "qos_library" && (string?)e.Attribute("name") == library)
            .Elements().SingleOrDefault(e => e.Name.LocalName == "qos_profile" && (string?)e.Attribute("name") == parts[^1]);
        return parent is null ? null : ResolvePolicyValue(parent, endpoint, policy, value, visited);
    }

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
