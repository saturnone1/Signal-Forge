namespace GrpcWorkbench.Models.Dds;

public enum DdsTriggerType
{
    /// <summary>주기적으로 토픽 발행</summary>
    Periodic,
    /// <summary>수동 1회 다발 발행 (N개 sample)</summary>
    Bulk,
    /// <summary>특정 토픽 수신 시 다른 토픽으로 자동 발행 (echo/transform)</summary>
    OnIncoming,
}

public sealed class DdsTrigger
{
    public string Id { get; init; } = System.Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public required string SessionId { get; init; }

    public DdsTriggerType Type { get; set; } = DdsTriggerType.Periodic;
    public bool Enabled { get; set; } = false;

    // 발행 대상
    public string TopicName { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string QosProfileName { get; set; } = "ReliableRealtime";
    public string JsonPayload { get; set; } = "{}";

    // Periodic
    public int IntervalMs { get; set; } = 1000;
    public int? MaxFires { get; set; }

    // Bulk
    public int BulkCount { get; set; } = 10;
    public bool BulkParallel { get; set; } = false;

    // OnIncoming — 매칭할 source topic (DDS)
    public string? MatchTopicName { get; set; }

    // 통계
    public long TotalFires;
    public long Errors;
    public DateTime? LastFiredAt { get; set; }
    public string? LastError { get; set; }
}
