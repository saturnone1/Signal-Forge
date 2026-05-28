namespace ASAP.Models.Nats;

public class NatsMessageEntry
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public string Subject { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string PayloadText { get; set; } = string.Empty;
    public int PayloadSize { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}