namespace ASAP.Models.Dds;

public sealed class DdsTransportSettings
{
    public int DomainId { get; set; }
    public string? ParticipantQosProfile { get; set; }
    // 구형 저장 설정의 호환 필드다. 송신 여부와 발행 전 대기에는 사용하지 않는다.
    public int RequiredSubscribers { get; set; }
    public int DiscoveryTimeoutMs { get; set; }
    public DdsDiscoveryMode DiscoveryMode { get; set; } = DdsDiscoveryMode.FromQos;
    public List<string> DiscoveryPeers { get; set; } = [];
    public string? MulticastAddress { get; set; }
    public List<string> AllowInterfaces { get; set; } = [];
    public List<string> DenyInterfaces { get; set; } = [];
    public int? SendBufferSize { get; set; }
    public int? ReceiveBufferSize { get; set; }

    internal DdsTransportSettings Snapshot() => new()
    {
        DomainId = DomainId, RequiredSubscribers = RequiredSubscribers, DiscoveryTimeoutMs = DiscoveryTimeoutMs,
        ParticipantQosProfile = ParticipantQosProfile,
        DiscoveryMode = DiscoveryMode, MulticastAddress = MulticastAddress,
        DiscoveryPeers = [.. DiscoveryPeers],
        AllowInterfaces = [.. AllowInterfaces], DenyInterfaces = [.. DenyInterfaces],
        SendBufferSize = SendBufferSize, ReceiveBufferSize = ReceiveBufferSize,
    };
}

public enum DdsDiscoveryMode
{
    Unicast,
    Multicast,
    FromQos,
}
