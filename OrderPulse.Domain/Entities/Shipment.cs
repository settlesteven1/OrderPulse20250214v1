using OrderPulse.Domain.Enums;

namespace OrderPulse.Domain.Entities;

public class Shipment
{
    public Guid ShipmentId { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public string? Carrier { get; set; }
    public string? CarrierNormalized { get; set; }
    public string? TrackingNumber { get; set; }
    public string? TrackingUrl { get; set; }
    public DateTime? ShipDate { get; set; }
    public DateOnly? EstimatedDelivery { get; set; }
    public ShipmentStatus Status { get; set; } = ShipmentStatus.Shipped;
    public string? LastStatusUpdate { get; set; }
    public DateTime? LastStatusDate { get; set; }
    public Guid? SourceEmailId { get; set; }
    /// <summary>
    /// JSON-serialized parsed shipment item data from the original email.
    /// Used for reconciliation when shipment processes before order confirmation.
    /// </summary>
    public string? ParsedItemsJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Order Order { get; set; } = null!;
    public EmailMessage? SourceEmail { get; set; }
    public ICollection<ShipmentLine> Lines { get; set; } = new List<ShipmentLine>();
    public Delivery? Delivery { get; set; }
}

public class ShipmentLine
{
    public Guid ShipmentLineId { get; set; }
    public Guid ShipmentId { get; set; }
    public Guid OrderLineId { get; set; }
    public int Quantity { get; set; } = 1;

    // Navigation
    public Shipment Shipment { get; set; } = null!;
    public OrderLine OrderLine { get; set; } = null!;
}
