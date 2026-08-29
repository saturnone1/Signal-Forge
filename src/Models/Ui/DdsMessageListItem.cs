namespace ASAP.Models.Ui;

public sealed class DdsMessageListItem
{
    public required string Id { get; init; }
    public required string Direction { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string TopicName { get; init; }
    public required string TypeName { get; init; }
    public required string JsonPayload { get; init; }
    public required bool Success { get; init; }
    public long? SequenceNumber { get; init; }
    public double? LatencyMs { get; init; }
    public long? SourceTimestampNs { get; init; }
    public string? PublicationHandle { get; init; }
    public string? Error { get; init; }
}
