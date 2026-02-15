using OrderPulse.Domain.Enums;

namespace OrderPulse.Domain.Entities;

public class OrderLine
{
    public Guid OrderLineId { get; set; }
    public Guid OrderId { get; set; }
    public int LineNumber { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? ProductUrl { get; set; }
    public string? SKU { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal? UnitPrice { get; set; }
    public decimal? LineTotal { get; set; }
    public OrderLineStatus Status { get; set; } = OrderLineStatus.Ordered;
    public string? ImageUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Order Order { get; set; } = null!;
    public ICollection<ShipmentLine> ShipmentLines { get; set; } = new List<ShipmentLine>();
    public ICollection<ReturnLine> ReturnLines { get; set; } = new List<ReturnLine>();
}
