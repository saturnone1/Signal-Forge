namespace GrpcWorkbench.Models.Dds;

public sealed class DdsSampleEntry
{
    public required long SequenceNumber { get; init; }
    public required string TopicName { get; init; }
    public required string TypeName { get; init; }
    public required DateTime ReceivedAt { get; init; }
    public required string JsonData { get; init; }

    // DDS source timestamp (nanoseconds since epoch); 0이면 미사용
    public long SourceTimestampNs { get; init; }

    // 발행자 식별 (가능한 경우)
    public string? PublicationHandle { get; init; }
}
