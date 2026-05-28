namespace ASAP.Models.Dds;

public enum DdsStepMode
{
    Manual,
    Periodic,
    Bulk,
    OnIncoming,
}

public sealed class DdsScenarioStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string TopicName { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string QosProfileName { get; set; } = string.Empty;
    public string JsonPayload { get; set; } = "{}";

    // Step-level controls
    public int RepeatCount { get; set; } = 1;
    public int DelayAfterMs { get; set; } = 0;

    // Automation controls
    public DdsStepMode Mode { get; set; } = DdsStepMode.Manual;
    public bool AutoEnabled { get; set; } = false;
    public int IntervalMs { get; set; } = 1000;
    public int? MaxFires { get; set; }
    public int BulkCount { get; set; } = 10;
    public bool BulkParallel { get; set; } = false;
    public string? MatchTopicName { get; set; }
    public bool BlockSelfTopicLoop { get; set; } = true;
    public int MinFireIntervalMs { get; set; } = 0;
    public int MaxFiresPerMinute { get; set; } = 0;
}
