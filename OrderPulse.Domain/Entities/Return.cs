using OrderPulse.Domain.Enums;

namespace OrderPulse.Domain.Entities;

public class Return
{
    public Guid ReturnId { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public string? RMANumber { get; set; }
    public ReturnStatus Status { get; set; } = ReturnStatus.Initiated;
    public string? ReturnReason { get; set; }
    public ReturnMethod? ReturnMethod { get; set; }
    public string? ReturnCarrier { get; set; }
    public string? ReturnTrackingNumber { get; set; }
    public string? ReturnTrackingUrl { get; set; }
    public string? ReturnLabelBlobUrl { get; set; }
    public string? QRCodeBlobUrl { get; set; }
    public string? QRCodeData { get; set; }
    public string? DropOffLocation { get; set; }
    public string? DropOffAddress { get; set; }
    public DateOnly? ReturnByDate { get; set; }
    public DateOnly? ReceivedByRetailerDate { get; set; }
    public string? RejectionReason { get; set; }
    public Guid? SourceEmailId { get; set; }
    public Guid? LastUpdatedEmailId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Order Order { get; set; } = null!;
    public EmailMessage? SourceEmail { get; set; }
    public ICollection<ReturnLine> Lines { get; set; } = new List<ReturnLine>();
    public Refund? Refund { get; set; }
}

public class ReturnLine
{
    public Guid ReturnLineId { get; set; }
    public Guid ReturnId { get; set; }
    public Guid OrderLineId { get; set; }
    public int Quantity { get; set; } = 1;
    public string? ReturnReason { get; set; }

    // Navigation
    public Return Return { get; set; } = null!;
    public OrderLine OrderLine { get; set; } = null!;
}
