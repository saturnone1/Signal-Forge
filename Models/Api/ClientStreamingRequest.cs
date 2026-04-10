namespace GrpcWorkbench.Models.Api;

public class ClientStreamingRequest
{
    public GrpcRequestPayload Payload { get; set; } = new();
    public List<string> Messages { get; set; } = [];
    public int IntervalMs { get; set; } = 500;
}
