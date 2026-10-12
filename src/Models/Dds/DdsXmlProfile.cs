using System.Text.Json.Serialization;

namespace ASAP.Models.Dds;

public sealed class DdsXmlProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "DDS 프로필";
    public string? ModuleName { get; set; }
    public string? QosLibraryName { get; set; }
    public string? ParticipantQosProfile { get; set; }
    // 목록 파일이 가리키는 불변 XML 묶음 버전이다. null은 구형 저장 경로를 뜻한다.
    public string? StorageRevision { get; set; }
    public string DdsSimXml { get; set; } = string.Empty;
    public string TopicSettingsJson { get; set; } = string.Empty;
    [JsonPropertyName("topicsXml")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyTopicsXml { get; set; }
    public string QosProfilesXml { get; set; } = string.Empty;

    // v1 저장소 읽기 전용 호환 필드. Normalize에서 연결 설정과 QoS로 변환 후 제거한다.
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
        ModuleName = ModuleName, QosLibraryName = QosLibraryName, ParticipantQosProfile = ParticipantQosProfile,
        DdsSimXml = DdsSimXml,
        TopicSettingsJson = TopicSettingsJson,
        QosProfilesXml = QosProfilesXml,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };
}

public sealed class DdsProfileCatalog
{
    public int Version { get; set; } = 4;
    public long Revision { get; set; }
    public List<DdsXmlProfile> Profiles { get; set; } = [];
}

public sealed record DdsProfileValidationResult(int TypeCount, int TopicCount, int QosProfileCount);
