using OrderPulse.Domain.Enums;

namespace OrderPulse.Domain.Entities;

public class Delivery
{
    public Guid DeliveryId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ShipmentId { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public string? DeliveryLocation { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Delivered;
    public DeliveryIssueType? IssueType { get; set; }
    public string? IssueDescription { get; set; }
    public string? PhotoBlobUrl { get; set; }
    public Guid? SourceEmailId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Shipment Shipment { get; set; } = null!;
    public EmailMessage? SourceEmail { get; set; }
}
