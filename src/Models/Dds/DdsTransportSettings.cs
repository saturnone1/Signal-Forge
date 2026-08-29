namespace ASAP.Models.Dds;

public sealed class DdsTransportSettings
{
    public int DomainId { get; set; }
    public DdsDiscoveryMode DiscoveryMode { get; set; } = DdsDiscoveryMode.Unicast;
    public string? MulticastAddress { get; set; }
    public List<string> AllowInterfaces { get; set; } = [];
    public List<string> DenyInterfaces { get; set; } = [];
    public int? SendBufferSize { get; set; }
    public int? ReceiveBufferSize { get; set; }
}

public enum DdsDiscoveryMode
{
    Unicast,
    Multicast,
}
