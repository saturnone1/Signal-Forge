using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using ASAP.Models.Dds;

namespace ASAP.Dds;

/// <summary>앱의 토픽 선택 정보다. DDSClient에는 이 목록을 코드 기반 등록으로 전달한다.</summary>
public static class DdsTopicSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    public static string Write(IEnumerable<DdsTopicConfig> topics) => JsonSerializer.Serialize(topics, Options);
    public static List<DdsTopicConfig> Read(string json) =>
        JsonSerializer.Deserialize<List<DdsTopicConfig>>(json, Options)
        ?? throw new InvalidOperationException("토픽 설정 목록이 없습니다.");

    // 구형 저장 프로필을 읽을 때만 사용한다. 새 프로필과 연결 설정은 XML을 만들지 않는다.
    public static string Migrate(string value)
    {
        if (!value.TrimStart().StartsWith('<')) return value;
        var root = XDocument.Parse(value).Root;
        if (root?.Name.LocalName != "topics") throw new InvalidOperationException("구형 토픽 설정 형식이 잘못되었습니다.");
        return Write(root.Elements("topic").Select(topic => new DdsTopicConfig
        {
            TopicName = (string?)topic.Attribute("name") ?? "",
            TypeName = (string?)topic.Attribute("type_name") ?? "",
            QosProfileName = (string?)topic.Attribute("qos_profile") ?? "",
            Direction = (string?)topic.Attribute("direction") switch
            {
                nameof(DdsTopicDirection.Both) => DdsTopicDirection.Both,
                nameof(DdsTopicDirection.Publish) => DdsTopicDirection.Publish,
                nameof(DdsTopicDirection.Subscribe) => DdsTopicDirection.Subscribe,
                _ => throw new InvalidOperationException("구형 토픽의 송수신 방향이 잘못되었습니다."),
            },
        }));
    }
}
