namespace GrpcWorkbench.Models.Api;

public class GrpcRequestPayload
{
    public string SessionId { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public Dictionary<string, string>? Metadata { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}
