namespace GrpcWorkbench.Models.Triggers;

public enum TriggerType
{
    Periodic,       // IntervalMs 마다 발사 (MaxFires 도달 시 자동 disable)
    OnIncoming,     // 매칭되는 수신 메시지마다 발사
    Bulk            // 수동 발사: N건 (순차/병렬)
}

public enum IncomingMatchOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    EndsWith,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}

/// <summary>
/// 자동 송신 규칙. 등록만 해 두면 TriggerExecutor가 백그라운드에서 발사.
/// Stats(TotalFires/Errors/Counter)는 Interlocked로 갱신되므로 field 선언.
/// </summary>
public sealed class Trigger
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Scenario { get; set; } = "";
    public int StepOrder { get; set; } = 1;
    public TriggerType Type { get; set; } = TriggerType.Periodic;
    public bool Enabled { get; set; }

    // 발사 대상 (Service.Method 단위, 현재 활성 세션 사용)
    public string TargetService { get; set; } = "";
    public string TargetMethod { get; set; } = "";

    // {{counter}} / {{now}} / OnIncoming은 {{incoming.<dotted-path>}} 치환
    public string PayloadTemplate { get; set; } = "{}";

    // Periodic
    public int IntervalMs { get; set; } = 1000;
    public int? MaxFires { get; set; }              // null = 무한

    // Bulk
    public int BulkCount { get; set; } = 10;
    public bool BulkParallel { get; set; }

    // OnIncoming — 빈 문자열이면 임의 매치(전체)
    public string MatchService { get; set; } = "";
    public string MatchMethod { get; set; } = "";
    public string MatchJsonPath { get; set; } = "";
    public IncomingMatchOperator MatchOperator { get; set; } = IncomingMatchOperator.Equals;
    public string MatchValue { get; set; } = "";

    // ── Stats (Interlocked 접근용 field) ──────────────────────────────────
    public long TotalFires;
    public long Errors;
    public long Counter;

    public DateTime? LastFiredAt { get; set; }
    public string? LastError { get; set; }

    // UI 전용
    public bool Editing { get; set; }
}
