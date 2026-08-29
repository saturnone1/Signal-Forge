using ASAP.Models.Dds;

namespace ASAP.Models.Session;

public class DdsSession
{
    public string SessionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    // 세션 생성 시점의 프로필 식별 정보. XML 본문은 아래 byte[]에 독립 스냅샷으로 보관한다.
    public string ProfileId { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public DateTimeOffset ProfileUpdatedAtUtc { get; set; }

    public DdsTransportSettings Transport { get; set; } = new();

    // DDSClient와 동일한 세 정의 파일의 세션 생성 시점 스냅샷
    public byte[]? DdsSimXmlContent { get; set; }
    public byte[]? TopicsXmlContent { get; set; }
    public byte[]? QosProfilesXmlContent { get; set; }

    // topics.xml에서 파싱된 토픽
    public List<DdsTopicConfig> Topics { get; set; } = [];

    // DDSSim.xml에서 파싱된 타입 (QualifiedName 키, 예: "MSG::AirThreatInformation")
    public Dictionary<string, DdsTypeDefinition> Types { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // qos_profiles.xml에서 추출한 QoS 프로파일
    public List<string> QosProfiles { get; set; } = [];
    public string QosLibraryName { get; set; } = string.Empty;

    // DDS 시나리오 (세션 범위 유지)
    public List<DdsScenarioStep> ScenarioSteps { get; set; } = [];

    // 세션/시스템 로그 (세션 범위 유지)
    public List<DdsSessionLogEntry> LogEntries { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
}
