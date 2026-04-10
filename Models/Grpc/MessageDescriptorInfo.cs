namespace GrpcWorkbench.Models.Grpc;

public class MessageDescriptorInfo
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, FieldInfo> Fields { get; set; } = [];
    public string? JsonSchema { get; set; }
}

public class FieldInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsRepeated { get; set; }
    public bool IsOptional { get; set; }
    public int FieldNumber { get; set; }
}
