using System.Xml.Linq;
using GrpcWorkbench.Models.Dds;

namespace GrpcWorkbench.Dds;

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
        string? QosProfilesXml);

    public static ParseResult Parse(string xmlContent)
    {
        var doc = XDocument.Parse(xmlContent);
        var root = doc.Root ?? throw new InvalidOperationException("dds-config.xml: root 엘리먼트 없음");

        // 두 root 형식 모두 처리
        var ddsElement = root.Name.LocalName == "dds"
            ? root
            : root.Element("dds");

        var topicsElement = root.Element("topics");

        var qosProfileNames = new List<string>();
        string? qosProfilesXml = null;

        if (ddsElement is not null)
        {
            var qosLibrary = ddsElement.Element("qos_library");
            if (qosLibrary is not null)
            {
                foreach (var profile in qosLibrary.Elements("qos_profile"))
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
            foreach (var t in topicsElement.Elements("topic"))
            {
                var topicName = t.Element("topic_name")?.Value?.Trim();
                var typeName = t.Element("type_name")?.Value?.Trim();
                var directionRaw = t.Element("direction")?.Value?.Trim();
                var qos = t.Element("qos_profile")?.Value?.Trim();

                if (string.IsNullOrEmpty(topicName) ||
                    string.IsNullOrEmpty(typeName) ||
                    string.IsNullOrEmpty(directionRaw) ||
                    string.IsNullOrEmpty(qos))
                    continue;

                if (!Enum.TryParse<DdsTopicDirection>(directionRaw, ignoreCase: true, out var direction))
                    direction = DdsTopicDirection.Both;

                topics.Add(new DdsTopicConfig
                {
                    TopicName = topicName,
                    TypeName = typeName,
                    Direction = direction,
                    QosProfileName = qos,
                });
            }
        }

        return new ParseResult(topics, qosProfileNames, qosProfilesXml);
    }
}
