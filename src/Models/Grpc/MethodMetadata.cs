namespace ASAP.Models.Grpc;

public class MethodMetadata
{
    public string MethodName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string RpcType { get; set; } = string.Empty;
    public string InputType { get; set; } = string.Empty;
    public string OutputType { get; set; } = string.Empty;
    public string? InputSchema { get; set; }
    public string? OutputSchema { get; set; }
}

public enum RpcTypeEnum
{
    Unary,
    ClientStreaming,
    ServerStreaming,
    BidirectionalStreaming
}
