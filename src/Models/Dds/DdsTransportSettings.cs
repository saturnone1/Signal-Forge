namespace ASAP.Models.Dds;

public sealed class DdsTransportSettings
{
    public int DomainId { get; set; }
    public string? MulticastAddress { get; set; }
    public List<string> AllowInterfaces { get; set; } = [];
    public List<string> DenyInterfaces { get; set; } = [];
    public int? SendBufferSize { get; set; }
    public int? ReceiveBufferSize { get; set; }
}
