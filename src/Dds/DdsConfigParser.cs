using System.Xml.Linq;
using ASAP.Models.Dds;

namespace ASAP.Dds;

/// <summary>
/// dds-config.xml (ddsAmbassador unified format) 파싱.
/// 두 형식 모두 지원:
///   1. <dds-ambassador-config> wrapper — ambassador 호환
///   2. 순수 <dds> root — RTI 표준 형식 (qos_library만 있을 때)
/// </summary>
public static class DdsConfigParser
{
    public sealed record ParseResult(
        IReadOnlyList<DdsTopicConfig> Topics,
        IReadOnlyList<string> QosProfileNames,
        string QosLibraryName,
        string? QosProfilesXml);

    public static ParseResult Parse(string xmlContent)
    {
        var doc = XDocument.Parse(xmlContent);
        var root = doc.Root ?? throw new InvalidOperationException("dds-config.xml: root 엘리먼트 없음");

        // 두 root 형식 모두 처리
        var ddsElement = root.Name.LocalName == "dds"
            ? root
            : Child(root, "dds");

        var topicsElement = Child(root, "topics");

        var qosProfileNames = new List<string>();
        var qosLibraryName = string.Empty;
        string? qosProfilesXml = null;

        if (ddsElement is not null)
        {
            var libraries = Children(ddsElement, "qos_library").ToList();
            if (libraries.Count > 1)
                throw new InvalidOperationException("Signal Forge 프로필에서는 QoS 라이브러리를 하나만 사용할 수 있습니다.");
            var qosLibrary = libraries.SingleOrDefault();
            if (qosLibrary is not null)
            {
                qosLibraryName = qosLibrary.Attribute("name")?.Value?.Trim() ?? string.Empty;
                foreach (var profile in Children(qosLibrary, "qos_profile"))
                {
                    var name = profile.Attribute("name")?.Value;
                    if (!string.IsNullOrWhiteSpace(name))
                        qosProfileNames.Add(name);
                }

                // RTI QosProvider에 그대로 넘길 수 있는 standalone XML 추출
                qosProfilesXml = new XDocument(
                    new XDeclaration("1.0", "UTF-8", null),
                    new XElement("dds", new XElement(qosLibrary))).ToString();
            }
        }

        var topics = new List<DdsTopicConfig>();
        if (topicsElement is not null)
        {
            foreach (var t in Children(topicsElement, "topic"))
            {
                var topicName = Child(t, "topic_name")?.Value?.Trim();
                var typeName = Child(t, "type_name")?.Value?.Trim();
                var directionRaw = Child(t, "direction")?.Value?.Trim();
                var qos = Child(t, "qos_profile")?.Value?.Trim();

                if (string.IsNullOrEmpty(topicName) ||
                    string.IsNullOrEmpty(typeName) ||
                    string.IsNullOrEmpty(directionRaw) ||
                    string.IsNullOrEmpty(qos))
                    continue;

                if (!Enum.TryParse<DdsTopicDirection>(directionRaw, ignoreCase: true, out var direction))
                    throw new InvalidOperationException($"토픽 '{topicName}'의 direction 값이 올바르지 않습니다: {directionRaw}");

                topics.Add(new DdsTopicConfig
                {
                    TopicName = topicName,
                    TypeName = typeName,
                    Direction = direction,
                    QosProfileName = qos,
                });
            }
        }

        return new ParseResult(topics, qosProfileNames, qosLibraryName, qosProfilesXml);
    }

    private static XElement? Child(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement parent, string localName)
        => parent.Elements().Where(element => element.Name.LocalName == localName);
}
