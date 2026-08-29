using System.Text.Json.Serialization;

namespace ASAP.Models.Dds;

public sealed class DdsXmlProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "DDS 프로필";
    public string DdsSimXml { get; set; } = string.Empty;
    public string TopicsXml { get; set; } = string.Empty;
    public string QosProfilesXml { get; set; } = string.Empty;

    // v1 저장소 읽기 전용 호환 필드. Normalize에서 3파일 형식으로 변환 후 제거한다.
    [JsonPropertyName("typesXml")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyTypesXml { get; set; }

    [JsonPropertyName("configXml")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyConfigXml { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DdsXmlProfile Copy(string? name = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name ?? Name,
        DdsSimXml = DdsSimXml,
        TopicsXml = TopicsXml,
        QosProfilesXml = QosProfilesXml,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };
}

public sealed class DdsProfileCatalog
{
    public int Version { get; set; } = 2;
    public long Revision { get; set; }
    public List<DdsXmlProfile> Profiles { get; set; } = [];
}

public sealed record DdsProfileValidationResult(int TypeCount, int TopicCount, int QosProfileCount);
