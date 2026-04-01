namespace OrderPulse.Domain.Entities;

public class InventoryAdjustment
{
    public Guid AdjustmentId { get; set; }
    public Guid InventoryItemId { get; set; }
    public Guid TenantId { get; set; }
    public int QuantityDelta { get; set; }
    public int PreviousQuantity { get; set; }
    public int NewQuantity { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? AdjustedBy { get; set; }
    public DateTime AdjustedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public InventoryItem InventoryItem { get; set; } = null!;
    public Tenant Tenant { get; set; } = null!;
}
