namespace ASAP.Models.Nats;

public class NatsSubscriptionInfo
{
    public string Subject { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}