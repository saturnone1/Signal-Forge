namespace ASAP.Models.Dds;

public enum DdsTopicDirection { Publish, Subscribe, Both }

public sealed class DdsTopicConfig
{
    public required string TopicName { get; init; }
    public required string TypeName { get; init; }
    public required DdsTopicDirection Direction { get; init; }
    public required string QosProfileName { get; init; }
}
