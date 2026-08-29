using System.Xml.Linq;
using ASAP.Models.Dds;

namespace ASAP.Dds;

/// <summary>
/// DDSClient의 definitions/topics.xml 및 definitions/qos_profiles.xml 계약을 파싱한다.
/// topic 이름은 DDSSim.xml의 MSG struct 이름이며 런타임 타입은 MSG::{name}이다.
/// </summary>
public static class DdsConfigParser
{
    public const string RequiredQosLibraryName = "AmbassadorProfiles";

    public sealed record ParseResult(
        IReadOnlyList<DdsTopicConfig> Topics,
        IReadOnlyList<string> QosProfileNames,
        string QosLibraryName,
        string QosProfilesXml);

    public static ParseResult Parse(string topicsXml, string qosProfilesXml)
    {
        var qosDocument = XDocument.Parse(qosProfilesXml);
        if (qosDocument.Root?.Name.LocalName != "dds")
            throw new InvalidOperationException("qos_profiles.xml root는 <dds>여야 합니다.");

        var library = qosDocument.Descendants("qos_library")
            .FirstOrDefault(element => string.Equals(
                element.Attribute("name")?.Value,
                RequiredQosLibraryName,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"qos_profiles.xml에 <qos_library name=\"{RequiredQosLibraryName}\">가 필요합니다.");

        var qosNames = library.Elements("qos_profile")
            .Select(element => element.Attribute("name")?.Value?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();
        if (qosNames.Count == 0)
            throw new InvalidOperationException($"QoS 라이브러리 '{RequiredQosLibraryName}'에 프로필이 없습니다.");
        if (qosNames.Distinct(StringComparer.Ordinal).Count() != qosNames.Count)
            throw new InvalidOperationException("qos_profiles.xml에 중복 QoS 프로필 이름이 있습니다.");

        var topicsDocument = XDocument.Parse(topicsXml);
        if (topicsDocument.Root?.Name.LocalName != "topics")
            throw new InvalidOperationException("topics.xml root는 <topics>여야 합니다.");

        var topics = new List<DdsTopicConfig>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var qosSet = qosNames.ToHashSet(StringComparer.Ordinal);
        foreach (var element in topicsDocument.Root.Elements("topic"))
        {
            var name = RequiredAttribute(element, "name");
            var qos = RequiredAttribute(element, "qos_profile");
            var directionText = RequiredAttribute(element, "direction");
            if (!seen.Add(name))
                throw new InvalidOperationException($"topics.xml에 중복 토픽이 있습니다: {name}");
            if (!qosSet.Contains(qos))
                throw new InvalidOperationException(
                    $"토픽 '{name}'이 없는 QoS '{RequiredQosLibraryName}::{qos}'을 참조합니다.");
            if (directionText is not nameof(DdsTopicDirection.Both) and
                not nameof(DdsTopicDirection.Publish) and
                not nameof(DdsTopicDirection.Subscribe))
                throw new InvalidOperationException(
                    $"토픽 '{name}'의 direction은 Both, Publish, Subscribe 중 하나여야 합니다: {directionText}");
            var direction = Enum.Parse<DdsTopicDirection>(directionText, ignoreCase: false);

            topics.Add(new DdsTopicConfig
            {
                TopicName = name,
                TypeName = $"MSG::{name}",
                Direction = direction,
                QosProfileName = qos,
            });
        }
        if (topics.Count == 0)
            throw new InvalidOperationException("topics.xml에 <topic>을 하나 이상 정의하세요.");

        return new ParseResult(topics, qosNames, RequiredQosLibraryName, qosProfilesXml);
    }

    private static string RequiredAttribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"topics.xml의 <topic>에 '{name}' 속성이 필요합니다.");
        return value;
    }
}
