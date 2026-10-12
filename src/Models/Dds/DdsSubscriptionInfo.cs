namespace ASAP.Models.Dds;

public enum DdsSubscriptionState { Active, Faulted, Closing, Closed }

public sealed class DdsSubscriptionInfo
{
    public required string SubscriptionId { get; init; }
    public required string SessionId { get; init; }
    public required string TopicName { get; init; }
    public required string TypeName { get; init; }
    public required DateTime StartedAt { get; init; }

    public long ReceivedCount;     // Interlocked로 증가
    public DdsSampleEntry? LastSample { get; set; }
    private int _state;
    public DdsSubscriptionState State { get => (DdsSubscriptionState)Volatile.Read(ref _state); internal set => Volatile.Write(ref _state, (int)value); }
    public bool IsActive { get => State == DdsSubscriptionState.Active; set => State = value ? DdsSubscriptionState.Active : DdsSubscriptionState.Closed; }
    public string? LastError { get; internal set; }
    public string? NativeSubscriptionId { get; internal set; }
    public string QosProfileName { get; internal set; } = string.Empty;
}
