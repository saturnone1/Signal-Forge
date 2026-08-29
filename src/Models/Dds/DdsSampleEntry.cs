namespace ASAP.Models.Dds;

public sealed class DdsSampleEntry
{
    public required long SequenceNumber { get; init; }
    public required string TopicName { get; init; }
    public required string TypeName { get; init; }
    public required DateTime ReceivedAt { get; init; }
    public required string JsonData { get; init; }

    // DDS source timestamp와 수신 시각의 차이. 송수신 장비의 시계 동기화가 필요하다.
    public double? ReceiveLatencyMs { get; init; }

    // DDS source timestamp (nanoseconds since epoch); 0이면 미사용
    public long SourceTimestampNs { get; init; }

    // 발행자 식별 (가능한 경우)
    public string? PublicationHandle { get; init; }
}
