using GrpcWorkbench.Models.Grpc;

namespace GrpcWorkbench.Models.Session;

public class GrpcSession
{
    public string SessionId { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; } = 50051;
    public bool UseTls { get; set; } = false;
    public byte[]? ProtoContent { get; set; }
    public string? ProtoFileName { get; set; }
    public List<ServiceMetadata> Services { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
}

public class StreamingSessionState
{
    public string SessionId { get; set; } = string.Empty;
    public string StreamId { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public List<string> ReceivedMessages { get; set; } = [];
    public bool IsActive { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}
