namespace GrpcWorkbench.Models.Dds;

public sealed class DdsSessionLogEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Scope { get; set; } = "session";
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = string.Empty;
}
