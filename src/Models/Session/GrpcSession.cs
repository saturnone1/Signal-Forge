using ASAP.Models.Grpc;

namespace ASAP.Models.Session;

public class GrpcSession
{
    public string SessionId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? UnixSocketPath { get; set; }
    public byte[]? ProtoContent { get; set; }
    public string? ProtoFileName { get; set; }
    public List<ServiceMetadata> Services { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
}
