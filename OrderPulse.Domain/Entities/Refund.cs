namespace OrderPulse.Domain.Entities;

public class Refund
{
    public Guid RefundId { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid? ReturnId { get; set; }
    public decimal RefundAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? RefundMethod { get; set; }
    public DateTime? RefundDate { get; set; }
    public string? EstimatedArrival { get; set; }
    public string? TransactionId { get; set; }
    public Guid? SourceEmailId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Order Order { get; set; } = null!;
    public Return? Return { get; set; }
    public EmailMessage? SourceEmail { get; set; }
}
