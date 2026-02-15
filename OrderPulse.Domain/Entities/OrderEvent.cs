namespace OrderPulse.Domain.Entities;

public class OrderEvent
{
    public Guid EventId { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTime EventDate { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? Details { get; set; } // JSON
    public Guid? EmailMessageId { get; set; }
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Order Order { get; set; } = null!;
    public EmailMessage? EmailMessage { get; set; }
}
