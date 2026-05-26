namespace GrpcWorkbench.Models.Dds;

public sealed class DdsScenarioStep
{
    public string TopicName { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string QosProfileName { get; set; } = string.Empty;
    public string JsonPayload { get; set; } = "{}";

    // Step-level controls
    public int RepeatCount { get; set; } = 1;
    public int DelayAfterMs { get; set; } = 0;
}

public sealed class DdsScenarioRunOptions
{
    // Number of full scenario cycles to run
    public int CycleCount { get; set; } = 1;

    // Delay inserted between steps if step.DelayAfterMs is zero
    public int DefaultStepDelayMs { get; set; } = 0;

    // Stop all remaining execution on first failure
    public bool StopOnError { get; set; } = true;
}
