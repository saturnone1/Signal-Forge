using GrpcWorkbench.Models.Dds;

namespace GrpcWorkbench.Models.Session;

public class DdsSession
{
    public string SessionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

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

    // DDS 시나리오 (세션 범위 유지)
    public List<DdsScenarioStep> ScenarioSteps { get; set; } = [];
    public DdsScenarioRunOptions ScenarioOptions { get; set; } = new();

    // 세션/시스템 로그 (세션 범위 유지)
    public List<DdsSessionLogEntry> LogEntries { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
}
