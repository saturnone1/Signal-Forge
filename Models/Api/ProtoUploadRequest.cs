namespace GrpcWorkbench.Models.Api;

public class ProtoUploadRequest
{
    public string SessionId { get; set; } = string.Empty;
    public IFormFile ProtoFile { get; set; } = null!;
    public string? Address { get; set; }
    public int Port { get; set; } = 50051;
}
