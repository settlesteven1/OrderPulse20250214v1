using OrderPulse.Domain.Enums;

namespace OrderPulse.Domain.Entities;

public class Order
{
    public Guid OrderId { get; set; }
    public Guid TenantId { get; set; }
    public Guid? RetailerId { get; set; }
    public string ExternalOrderNumber { get; set; } = string.Empty;
    public string? ExternalOrderUrl { get; set; }
    public DateTime OrderDate { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Placed;
    public decimal? Subtotal { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? ShippingCost { get; set; }
    public decimal? DiscountAmount { get; set; }
    public decimal? TotalAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public DateOnly? EstimatedDeliveryStart { get; set; }
    public DateOnly? EstimatedDeliveryEnd { get; set; }
    public string? ShippingAddress { get; set; }
    public string? PaymentMethodSummary { get; set; }
    public bool IsInferred { get; set; }
    public Guid? SourceEmailId { get; set; }
    public Guid? LastUpdatedEmailId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Retailer? Retailer { get; set; }
    public EmailMessage? SourceEmail { get; set; }
    public EmailMessage? LastUpdatedEmail { get; set; }
    public ICollection<OrderLine> Lines { get; set; } = new List<OrderLine>();
    public ICollection<Shipment> Shipments { get; set; } = new List<Shipment>();
    public ICollection<Return> Returns { get; set; } = new List<Return>();
    public ICollection<Refund> Refunds { get; set; } = new List<Refund>();
    public ICollection<OrderEvent> Events { get; set; } = new List<OrderEvent>();
}
