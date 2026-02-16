using Microsoft.EntityFrameworkCore;
using OrderPulse.Domain.Entities;
using OrderPulse.Domain.Enums;
using OrderPulse.Domain.Interfaces;
using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Infrastructure.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly OrderPulseDbContext _db;

    public OrderRepository(OrderPulseDbContext db)
    {
        _db = db;
    }

    public async Task<Order?> GetByIdAsync(Guid orderId, CancellationToken ct = default)
    {
        return await _db.Orders
            .Include(o => o.Retailer)
            .Include(o => o.Lines)
            .Include(o => o.Shipments).ThenInclude(s => s.Lines)
            .Include(o => o.Shipments).ThenInclude(s => s.Delivery)
            .Include(o => o.Returns).ThenInclude(r => r.Lines)
            .Include(o => o.Refunds)
            .Include(o => o.Events.OrderByDescending(e => e.EventDate))
            .FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
    }

    public async Task<Order?> GetByExternalOrderNumberAsync(string externalOrderNumber, CancellationToken ct = default)
    {
        return await _db.Orders
            .Include(o => o.Retailer)
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.ExternalOrderNumber == externalOrderNumber, ct);
    }

    public async Task<(IReadOnlyList<Order> Items, int TotalCount)> GetOrdersAsync(
        OrderQueryParameters query, CancellationToken ct = default)
    {
        var q = _db.Orders
            .Include(o => o.Retailer)
            .Include(o => o.Lines)
            .AsQueryable();

        // Filter by status
        if (query.Status.HasValue)
        {
            q = q.Where(o => o.Status == query.Status.Value);
        }

        // Filter by status shortcut (groups of statuses)
        if (!string.IsNullOrEmpty(query.StatusShortcut))
        {
            q = query.StatusShortcut.ToLowerInvariant() switch
            {
                "awaiting-delivery" => q.Where(o =>
                    o.Status == OrderStatus.Placed ||
                    o.Status == OrderStatus.PartiallyShipped ||
                    o.Status == OrderStatus.Shipped ||
                    o.Status == OrderStatus.InTransit ||
                    o.Status == OrderStatus.OutForDelivery),
                "needs-attention" => q.Where(o =>
                    o.Status == OrderStatus.DeliveryException ||
                    o.Status == OrderStatus.ReturnInProgress ||
                    o.Status == OrderStatus.ReturnReceived),
                "completed" => q.Where(o =>
                    o.Status == OrderStatus.Delivered ||
                    o.Status == OrderStatus.Closed ||
                    o.Status == OrderStatus.Refunded),
                "cancelled" => q.Where(o =>
                    o.Status == OrderStatus.Cancelled ||
                    o.Status == OrderStatus.PartiallyCancelled),
                _ => q
            };
        }

        // Filter by retailer
        if (query.RetailerId.HasValue)
        {
            q = q.Where(o => o.RetailerId == query.RetailerId.Value);
        }

        // Filter by date range
        if (query.DateFrom.HasValue)
        {
            q = q.Where(o => o.OrderDate >= query.DateFrom.Value);
        }
        if (query.DateTo.HasValue)
        {
            q = q.Where(o => o.OrderDate <= query.DateTo.Value);
        }

        // Search by order number, retailer name, or product name
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.ToLower();
            q = q.Where(o =>
                o.ExternalOrderNumber.ToLower().Contains(search) ||
                (o.Retailer != null && o.Retailer.Name.ToLower().Contains(search)) ||
                o.Lines.Any(l => l.ProductName.ToLower().Contains(search)));
        }

        var totalCount = await q.CountAsync(ct);

        // Sorting
        q = query.SortBy.ToLowerInvariant() switch
        {
            "orderdate" => query.SortDescending
                ? q.OrderByDescending(o => o.OrderDate)
                : q.OrderBy(o => o.OrderDate),
            "totalamount" => query.SortDescending
                ? q.OrderByDescending(o => o.TotalAmount)
                : q.OrderBy(o => o.TotalAmount),
            "status" => query.SortDescending
                ? q.OrderByDescending(o => o.Status)
                : q.OrderBy(o => o.Status),
            "retailer" => query.SortDescending
                ? q.OrderByDescending(o => o.Retailer != null ? o.Retailer.Name : "")
                : q.OrderBy(o => o.Retailer != null ? o.Retailer.Name : ""),
            _ => query.SortDescending
                ? q.OrderByDescending(o => o.OrderDate)
                : q.OrderBy(o => o.OrderDate)
        };

        // Pagination
        var items = await q
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<Order> CreateAsync(Order order, CancellationToken ct = default)
    {
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);
        return order;
    }

    public async Task UpdateAsync(Order order, CancellationToken ct = default)
    {
        order.UpdatedAt = DateTime.UtcNow;
        _db.Orders.Update(order);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<Dictionary<OrderStatus, int>> GetStatusCountsAsync(CancellationToken ct = default)
    {
        return await _db.Orders
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);
    }
}
