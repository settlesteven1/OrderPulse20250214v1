using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderPulse.Api.DTOs;
using OrderPulse.Domain.Enums;
using OrderPulse.Infrastructure.Data;
using OrderPulse.Infrastructure.Services;

namespace OrderPulse.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class InventoryController : ControllerBase
{
    private readonly OrderPulseDbContext _db;
    private readonly InventoryService _inventoryService;

    public InventoryController(OrderPulseDbContext db, InventoryService inventoryService)
    {
        _db = db;
        _inventoryService = inventoryService;
    }

    /// <summary>
    /// List inventory items with optional filtering by category and search.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<InventoryItemDto>>>> GetInventory(
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var query = _db.InventoryItems
            .Include(i => i.Order)
            .AsQueryable();

        if (!string.IsNullOrEmpty(category) &&
            Enum.TryParse<ItemCategory>(category, ignoreCase: true, out var cat))
        {
            query = query.Where(i => i.ItemCategory == cat);
        }

        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(i => i.ProductName.Contains(search));
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(i => i.DeliveryDate ?? i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new InventoryItemDto(
                i.InventoryItemId,
                i.OrderLineId,
                i.OrderId,
                i.ProductName,
                i.ItemCategory.ToString(),
                i.QuantityOnHand,
                i.UnitStatus != null ? i.UnitStatus.ToString() : null,
                i.Condition != null ? i.Condition.ToString() : null,
                i.PurchaseDate,
                i.DeliveryDate,
                i.Order != null ? i.Order.ExternalOrderNumber : null,
                i.CreatedAt,
                i.UpdatedAt
            ))
            .ToListAsync(ct);

        return Ok(new ApiResponse<IReadOnlyList<InventoryItemDto>>(
            items, new PaginationMeta(page, pageSize, totalCount)));
    }

    /// <summary>
    /// Get a single inventory item with recent adjustments.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<InventoryItemDetailDto>>> GetInventoryItem(
        Guid id, CancellationToken ct)
    {
        var item = await _db.InventoryItems
            .Include(i => i.Order)
            .Include(i => i.Adjustments.OrderByDescending(a => a.AdjustedAt).Take(20))
            .FirstOrDefaultAsync(i => i.InventoryItemId == id, ct);

        if (item is null) return NotFound();

        var dto = new InventoryItemDetailDto(
            item.InventoryItemId,
            item.OrderLineId,
            item.OrderId,
            item.ProductName,
            item.ItemCategory.ToString(),
            item.QuantityOnHand,
            item.UnitStatus?.ToString(),
            item.Condition?.ToString(),
            item.PurchaseDate,
            item.DeliveryDate,
            item.Order?.ExternalOrderNumber,
            item.CreatedAt,
            item.UpdatedAt,
            item.Adjustments.Select(a => new InventoryAdjustmentDto(
                a.AdjustmentId,
                a.QuantityDelta,
                a.PreviousQuantity,
                a.NewQuantity,
                a.Reason,
                a.Notes,
                a.AdjustedBy,
                a.AdjustedAt
            )).ToList()
        );

        return Ok(new ApiResponse<InventoryItemDetailDto>(dto));
    }

    /// <summary>
    /// Adjust inventory quantity with reason.
    /// </summary>
    [HttpPost("{id:guid}/adjust")]
    public async Task<ActionResult<ApiResponse<InventoryAdjustmentDto>>> AdjustInventory(
        Guid id,
        [FromBody] AdjustInventoryRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new ApiError("INVALID_REASON", "Reason is required"));

        if (request.QuantityDelta == 0)
            return BadRequest(new ApiError("INVALID_DELTA", "Quantity change cannot be zero"));

        try
        {
            var username = User.Identity?.Name ?? "Unknown";
            var adjustment = await _inventoryService.AdjustInventoryAsync(
                id, request.QuantityDelta, request.Reason, request.Notes, username, ct);

            var dto = new InventoryAdjustmentDto(
                adjustment.AdjustmentId,
                adjustment.QuantityDelta,
                adjustment.PreviousQuantity,
                adjustment.NewQuantity,
                adjustment.Reason,
                adjustment.Notes,
                adjustment.AdjustedBy,
                adjustment.AdjustedAt
            );

            return Ok(new ApiResponse<InventoryAdjustmentDto>(dto));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ApiError("NOT_FOUND", ex.Message));
        }
    }

    /// <summary>
    /// Get adjustment history for an inventory item.
    /// </summary>
    [HttpGet("{id:guid}/adjustments")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<InventoryAdjustmentDto>>>> GetAdjustments(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = _db.InventoryAdjustments
            .Where(a => a.InventoryItemId == id)
            .OrderByDescending(a => a.AdjustedAt);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new InventoryAdjustmentDto(
                a.AdjustmentId,
                a.QuantityDelta,
                a.PreviousQuantity,
                a.NewQuantity,
                a.Reason,
                a.Notes,
                a.AdjustedBy,
                a.AdjustedAt
            ))
            .ToListAsync(ct);

        return Ok(new ApiResponse<IReadOnlyList<InventoryAdjustmentDto>>(
            items, new PaginationMeta(page, pageSize, totalCount)));
    }

    /// <summary>
    /// Update item category (toggle between Durable and Consumable).
    /// </summary>
    [HttpPut("{id:guid}/category")]
    public async Task<ActionResult> UpdateCategory(
        Guid id,
        [FromBody] UpdateInventoryCategoryRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Category) ||
            !Enum.TryParse<ItemCategory>(request.Category, ignoreCase: true, out var newCategory))
        {
            return BadRequest(new ApiError("INVALID_CATEGORY", "Category must be 'Durable' or 'Consumable'"));
        }

        var item = await _db.InventoryItems.FindAsync(new object[] { id }, ct);
        if (item is null) return NotFound(new ApiError("NOT_FOUND", "Inventory item not found"));

        if (item.ItemCategory == newCategory)
            return Ok(new { status = "unchanged", category = newCategory.ToString() });

        item.ItemCategory = newCategory;

        // Clear durable-specific fields when switching to consumable
        if (newCategory == ItemCategory.Consumable)
        {
            item.UnitStatus = null;
            item.Condition = null;
        }
        // Set durable defaults when switching to durable
        else
        {
            item.UnitStatus ??= InventoryUnitStatus.Owned;
            item.Condition ??= ItemCondition.New;
        }

        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new { status = "updated", category = newCategory.ToString() });
    }

    /// <summary>
    /// Get orders containing the same product as this inventory item.
    /// </summary>
    [HttpGet("{id:guid}/related-orders")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RelatedOrderDto>>>> GetRelatedOrders(Guid id, CancellationToken ct)
    {
        var item = await _db.InventoryItems.FindAsync(new object[] { id }, ct);
        if (item is null) return NotFound(new ApiError("NOT_FOUND", "Inventory item not found"));
        var productName = item.ProductName;
        var orders = await _db.Orders.Include(o => o.Retailer).Include(o => o.Lines)
            .Where(o => o.Lines.Any(l => l.ProductName == productName))
            .OrderByDescending(o => o.OrderDate)
            .Select(o => new RelatedOrderDto(o.OrderId, o.ExternalOrderNumber, o.OrderDate, o.Status.ToString(), o.TotalAmount, o.Currency, o.Retailer != null ? o.Retailer.Name : null,
                o.Lines.Where(l => l.ProductName == productName).Select(l => new RelatedOrderLineDto(l.ProductName, l.Quantity, l.UnitPrice, l.LineTotal)).ToList()))
            .ToListAsync(ct);
        return Ok(new ApiResponse<IReadOnlyList<RelatedOrderDto>>(orders));
    }

    /// <summary>
    /// Update durable item status/condition.
    /// </summary>
    [HttpPut("{id:guid}/status")]
    public async Task<ActionResult> UpdateStatus(
        Guid id,
        [FromBody] UpdateInventoryStatusRequest request,
        CancellationToken ct)
    {
        if (request.UnitStatus is not null &&
            Enum.TryParse<InventoryUnitStatus>(request.UnitStatus, ignoreCase: true, out var status))
        {
            await _inventoryService.UpdateUnitStatusAsync(id, status, ct);
        }

        if (request.Condition is not null &&
            Enum.TryParse<ItemCondition>(request.Condition, ignoreCase: true, out var condition))
        {
            await _inventoryService.UpdateConditionAsync(id, condition, ct);
        }

        return Ok();
    }
}
