using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Entities;
using OrderPulse.Domain.Enums;
using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Infrastructure.Services;

/// <summary>
/// Manages inventory lifecycle: creation on delivery, removal on return,
/// manual adjustments with audit logging.
/// </summary>
public class InventoryService
{
    private readonly OrderPulseDbContext _db;
    private readonly ILogger<InventoryService> _logger;

    public InventoryService(OrderPulseDbContext db, ILogger<InventoryService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Creates inventory items for all order lines linked to a delivered shipment.
    /// Called by the orchestrator after a Delivery record is confirmed.
    /// </summary>
    public async Task CreateInventoryForDeliveryAsync(
        Guid shipmentId, Guid tenantId, DateTime deliveryDate, CancellationToken ct = default)
    {
        var shipmentLines = await _db.ShipmentLines
            .IgnoreQueryFilters()
            .Include(sl => sl.OrderLine)
                .ThenInclude(ol => ol.Order)
            .Where(sl => sl.ShipmentId == shipmentId)
            .ToListAsync(ct);

        if (shipmentLines.Count == 0)
        {
            _logger.LogWarning("No shipment lines found for shipment {ShipmentId}, skipping inventory creation", shipmentId);
            return;
        }

        foreach (var sl in shipmentLines)
        {
            if (sl.OrderLine is null) continue;

            // Check if inventory already exists for this order line
            var existing = await _db.InventoryItems
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.OrderLineId == sl.OrderLine.OrderLineId, ct);

            if (existing is not null)
            {
                _logger.LogDebug("Inventory already exists for OrderLine {OrderLineId}, skipping", sl.OrderLine.OrderLineId);
                continue;
            }

            var category = sl.OrderLine.ItemCategory ?? ItemCategory.Durable;
            var inventoryItem = new InventoryItem
            {
                InventoryItemId = Guid.NewGuid(),
                TenantId = tenantId,
                OrderLineId = sl.OrderLine.OrderLineId,
                OrderId = sl.OrderLine.OrderId,
                ProductName = sl.OrderLine.ProductName,
                ItemCategory = category,
                PurchaseDate = sl.OrderLine.Order?.OrderDate,
                DeliveryDate = deliveryDate,
            };

            if (category == ItemCategory.Durable)
            {
                // For durables, each unit is individually tracked
                inventoryItem.QuantityOnHand = sl.Quantity;
                inventoryItem.UnitStatus = InventoryUnitStatus.Owned;
                inventoryItem.Condition = ItemCondition.New;
            }
            else
            {
                // For consumables, just track quantity
                inventoryItem.QuantityOnHand = sl.Quantity;
            }

            _db.InventoryItems.Add(inventoryItem);

            // Log the initial delivery adjustment
            _db.InventoryAdjustments.Add(new InventoryAdjustment
            {
                AdjustmentId = Guid.NewGuid(),
                InventoryItemId = inventoryItem.InventoryItemId,
                TenantId = tenantId,
                QuantityDelta = sl.Quantity,
                PreviousQuantity = 0,
                NewQuantity = sl.Quantity,
                Reason = "Delivery",
                Notes = $"Item delivered via shipment",
                AdjustedBy = "System",
                AdjustedAt = DateTime.UtcNow
            });
        }

        // If shipment has no lines but order has lines, create inventory from order lines directly
        if (shipmentLines.All(sl => sl.OrderLine is null))
        {
            var shipment = await _db.Shipments
                .IgnoreQueryFilters()
                .Include(s => s.Order)
                    .ThenInclude(o => o.Lines)
                .FirstOrDefaultAsync(s => s.ShipmentId == shipmentId, ct);

            if (shipment?.Order?.Lines is not null)
            {
                await CreateInventoryFromOrderLinesAsync(shipment.Order, tenantId, deliveryDate, ct);
            }
        }
    }

    /// <summary>
    /// Fallback: creates inventory items directly from order lines when no shipment lines exist.
    /// </summary>
    private async Task CreateInventoryFromOrderLinesAsync(
        Order order, Guid tenantId, DateTime deliveryDate, CancellationToken ct)
    {
        foreach (var line in order.Lines)
        {
            var existing = await _db.InventoryItems
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.OrderLineId == line.OrderLineId, ct);

            if (existing is not null) continue;

            var category = line.ItemCategory ?? ItemCategory.Durable;
            var inventoryItem = new InventoryItem
            {
                InventoryItemId = Guid.NewGuid(),
                TenantId = tenantId,
                OrderLineId = line.OrderLineId,
                OrderId = order.OrderId,
                ProductName = line.ProductName,
                ItemCategory = category,
                QuantityOnHand = line.Quantity,
                UnitStatus = category == ItemCategory.Durable ? InventoryUnitStatus.Owned : null,
                Condition = category == ItemCategory.Durable ? ItemCondition.New : null,
                PurchaseDate = order.OrderDate,
                DeliveryDate = deliveryDate,
            };

            _db.InventoryItems.Add(inventoryItem);

            _db.InventoryAdjustments.Add(new InventoryAdjustment
            {
                AdjustmentId = Guid.NewGuid(),
                InventoryItemId = inventoryItem.InventoryItemId,
                TenantId = tenantId,
                QuantityDelta = line.Quantity,
                PreviousQuantity = 0,
                NewQuantity = line.Quantity,
                Reason = "Delivery",
                Notes = "Item delivered (created from order lines)",
                AdjustedBy = "System",
                AdjustedAt = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Removes items from inventory when a return is processed.
    /// For durables: sets UnitStatus to Returned.
    /// For consumables: decrements QuantityOnHand.
    /// </summary>
    public async Task ProcessReturnInventoryAsync(
        Return returnEntity, Guid tenantId, CancellationToken ct = default)
    {
        foreach (var returnLine in returnEntity.Lines)
        {
            var inventoryItem = await _db.InventoryItems
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.OrderLineId == returnLine.OrderLineId, ct);

            if (inventoryItem is null)
            {
                _logger.LogDebug("No inventory item for OrderLine {OrderLineId} — nothing to return",
                    returnLine.OrderLineId);
                continue;
            }

            var previousQty = inventoryItem.QuantityOnHand;
            var delta = -returnLine.Quantity;

            if (inventoryItem.ItemCategory == ItemCategory.Durable)
            {
                inventoryItem.UnitStatus = InventoryUnitStatus.Returned;
                inventoryItem.QuantityOnHand = Math.Max(0, inventoryItem.QuantityOnHand - returnLine.Quantity);
            }
            else
            {
                inventoryItem.QuantityOnHand = Math.Max(0, inventoryItem.QuantityOnHand - returnLine.Quantity);
            }

            inventoryItem.UpdatedAt = DateTime.UtcNow;

            _db.InventoryAdjustments.Add(new InventoryAdjustment
            {
                AdjustmentId = Guid.NewGuid(),
                InventoryItemId = inventoryItem.InventoryItemId,
                TenantId = tenantId,
                QuantityDelta = delta,
                PreviousQuantity = previousQty,
                NewQuantity = inventoryItem.QuantityOnHand,
                Reason = "Return",
                Notes = $"Return processed (RMA: {returnEntity.RMANumber ?? "N/A"})",
                AdjustedBy = "System",
                AdjustedAt = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Manually adjusts inventory quantity with a reason for audit trail.
    /// Used by the API for user-initiated adjustments.
    /// </summary>
    public async Task<InventoryAdjustment> AdjustInventoryAsync(
        Guid inventoryItemId, int quantityDelta, string reason, string? notes,
        string? adjustedBy, CancellationToken ct = default)
    {
        var item = await _db.InventoryItems
            .FirstOrDefaultAsync(i => i.InventoryItemId == inventoryItemId, ct)
            ?? throw new InvalidOperationException($"Inventory item {inventoryItemId} not found");

        var previousQty = item.QuantityOnHand;
        item.QuantityOnHand = Math.Max(0, item.QuantityOnHand + quantityDelta);
        item.UpdatedAt = DateTime.UtcNow;

        // For durables, update unit status based on quantity
        if (item.ItemCategory == ItemCategory.Durable)
        {
            if (item.QuantityOnHand <= 0)
            {
                item.UnitStatus = reason.Equals("Damaged", StringComparison.OrdinalIgnoreCase)
                    ? InventoryUnitStatus.Damaged
                    : reason.Equals("Lost", StringComparison.OrdinalIgnoreCase)
                        ? InventoryUnitStatus.Lost
                        : reason.Equals("Disposed", StringComparison.OrdinalIgnoreCase)
                            ? InventoryUnitStatus.Disposed
                            : item.UnitStatus;
            }
            else
            {
                item.UnitStatus = InventoryUnitStatus.Owned;
            }
        }

        var adjustment = new InventoryAdjustment
        {
            AdjustmentId = Guid.NewGuid(),
            InventoryItemId = inventoryItemId,
            TenantId = item.TenantId,
            QuantityDelta = quantityDelta,
            PreviousQuantity = previousQty,
            NewQuantity = item.QuantityOnHand,
            Reason = reason,
            Notes = notes,
            AdjustedBy = adjustedBy,
            AdjustedAt = DateTime.UtcNow
        };

        _db.InventoryAdjustments.Add(adjustment);
        await _db.SaveChangesAsync(ct);

        return adjustment;
    }

    /// <summary>
    /// Updates condition for a durable inventory item.
    /// </summary>
    public async Task UpdateConditionAsync(
        Guid inventoryItemId, ItemCondition condition, CancellationToken ct = default)
    {
        var item = await _db.InventoryItems
            .FirstOrDefaultAsync(i => i.InventoryItemId == inventoryItemId, ct)
            ?? throw new InvalidOperationException($"Inventory item {inventoryItemId} not found");

        item.Condition = condition;
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Updates unit status for a durable inventory item.
    /// </summary>
    public async Task UpdateUnitStatusAsync(
        Guid inventoryItemId, InventoryUnitStatus status, CancellationToken ct = default)
    {
        var item = await _db.InventoryItems
            .FirstOrDefaultAsync(i => i.InventoryItemId == inventoryItemId, ct)
            ?? throw new InvalidOperationException($"Inventory item {inventoryItemId} not found");

        item.UnitStatus = status;
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
