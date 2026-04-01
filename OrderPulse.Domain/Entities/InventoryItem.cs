using OrderPulse.Domain.Enums;

namespace OrderPulse.Domain.Entities;

public class InventoryItem
{
    public Guid InventoryItemId { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderLineId { get; set; }
    public Guid OrderId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public ItemCategory ItemCategory { get; set; }
    public int QuantityOnHand { get; set; }
    public InventoryUnitStatus? UnitStatus { get; set; }
    public ItemCondition? Condition { get; set; }
    public DateTime? PurchaseDate { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Order Order { get; set; } = null!;
    public OrderLine OrderLine { get; set; } = null!;
    public ICollection<InventoryAdjustment> Adjustments { get; set; } = new List<InventoryAdjustment>();
}
