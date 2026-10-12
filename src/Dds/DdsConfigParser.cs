using System.Xml.Linq;
using ASAP.Models.Dds;

namespace ASAP.Dds;

/// <summary>
/// 토픽 목록과 외부 QoS XML을 검증한다. 토픽은 DDSClient에 코드로 등록한다.
/// </summary>
public static class DdsConfigParser
{
    public const string RequiredQosLibraryName = "AmbassadorProfiles";

    public sealed record ParseResult(
        IReadOnlyList<DdsTopicConfig> Topics,
        IReadOnlyList<string> QosProfileNames,
        string QosLibraryName,
        string QosProfilesXml);

    public static XElement SelectLibrary(XDocument document, string? name = null)
    {
        var libraries = document.Descendants("qos_library").ToArray();
        if (!string.IsNullOrWhiteSpace(name))
            return libraries.SingleOrDefault(library => (string?)library.Attribute("name") == name)
                ?? throw new InvalidOperationException($"QoS 라이브러리가 없습니다: {name}");
        if (libraries.Length == 1) return libraries[0];
        // 저장된 구형 프로필은 기존 기본 라이브러리를 사용한다. 그 외 복수 라이브러리는 명시적 선택이 필요하다.
        return libraries.SingleOrDefault(library => (string?)library.Attribute("name") == RequiredQosLibraryName)
            ?? throw new InvalidOperationException("여러 QoS 라이브러리가 있습니다. 사용할 라이브러리를 명시하세요.");
    }

    public static ParseResult Parse(string topicSettingsJson, string qosProfilesXml, BuiltMessageModule? module = null, string? qosLibraryName = null, bool validateBindings = true)
    {
        var qosDocument = XDocument.Parse(qosProfilesXml);
        if (qosDocument.Root?.Name.LocalName != "dds")
            throw new InvalidOperationException("qos_profiles.xml root는 <dds>여야 합니다.");

        var library = SelectLibrary(qosDocument, qosLibraryName);
        var libraryName = (string?)library.Attribute("name") ?? throw new InvalidOperationException("QoS 라이브러리에 이름이 필요합니다.");

        var qosNames = library.Elements("qos_profile")
            .Select(element => element.Attribute("name")?.Value?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();
        if (qosNames.Count == 0)
            throw new InvalidOperationException($"QoS 라이브러리 '{libraryName}'에 프로필이 없습니다.");
        if (qosNames.Distinct(StringComparer.Ordinal).Count() != qosNames.Count)
            throw new InvalidOperationException("qos_profiles.xml에 중복 QoS 프로필 이름이 있습니다.");

        var topics = DdsTopicSettings.Read(DdsTopicSettings.Migrate(topicSettingsJson));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var qosSet = qosNames.ToHashSet(StringComparer.Ordinal);
        var normalized = new List<DdsTopicConfig>();
        foreach (var topic in topics)
        {
            if (topic is null || string.IsNullOrWhiteSpace(topic.TopicName) || string.IsNullOrWhiteSpace(topic.QosProfileName))
                throw new InvalidOperationException("토픽 이름과 QoS 프로파일을 지정하세요.");
            if (!seen.Add(topic.TopicName)) throw new InvalidOperationException("중복 토픽: " + topic.TopicName);
            if (!Enum.IsDefined(topic.Direction)) throw new InvalidOperationException("잘못된 토픽 송수신 방향입니다.");
            var qos = topic.QosProfileName;
            if (qos.StartsWith(libraryName + "::", StringComparison.Ordinal)) qos = qos[(libraryName.Length + 2)..];
            if (validateBindings && !qosSet.Contains(qos))
                throw new InvalidOperationException($"토픽 '{topic.TopicName}'이 없는 QoS '{libraryName}::{qos}'을 참조합니다.");
            var typeName = topic.TypeName;
            if (string.IsNullOrWhiteSpace(typeName))
                typeName = module is not null && module.Module.Types.TryGetValue(topic.TopicName, out var type)
                    ? BuiltMessageRegistry.WireTypeName(type) : BuiltMessageRegistry.DefaultWireTypeName(topic.TopicName);
            if (module is not null && validateBindings) module.ResolveType(typeName);
            normalized.Add(new DdsTopicConfig { TopicName = topic.TopicName, TypeName = typeName,
                QosProfileName = qos, Direction = topic.Direction });
        }
        if (topics.Count == 0) throw new InvalidOperationException("토픽을 하나 이상 선택하세요.");
        return new ParseResult(normalized, qosNames, libraryName, qosProfilesXml);
    }

}
