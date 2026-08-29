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

    // 사용자가 업로드한 원본 XML — 재로드/내보내기용
    public byte[]? TypesXmlContent { get; set; }
    public string? TypesXmlFileName { get; set; }
    public byte[]? ConfigXmlContent { get; set; }
    public string? ConfigXmlFileName { get; set; }

    // ConfigXml에서 파싱된 토픽
    public List<DdsTopicConfig> Topics { get; set; } = [];

    // TypesXml에서 파싱된 타입 (QualifiedName 키, 예: "MSG::AirThreatInformation")
    public Dictionary<string, DdsTypeDefinition> Types { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // QoS 프로파일 이름 (config에서 추출)
    public List<string> QosProfiles { get; set; } = [];
    public string QosLibraryName { get; set; } = string.Empty;

    // DDS 시나리오 (세션 범위 유지)
    public List<DdsScenarioStep> ScenarioSteps { get; set; } = [];

    // 세션/시스템 로그 (세션 범위 유지)
    public List<DdsSessionLogEntry> LogEntries { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
}
