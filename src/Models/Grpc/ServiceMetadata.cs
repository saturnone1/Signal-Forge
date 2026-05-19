namespace GrpcWorkbench.Models.Grpc;

public class ServiceMetadata
{
    public string ServiceName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<MethodMetadata> Methods { get; set; } = [];
}
