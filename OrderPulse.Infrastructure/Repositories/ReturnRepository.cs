using Microsoft.EntityFrameworkCore;
using OrderPulse.Domain.Entities;
using OrderPulse.Domain.Enums;
using OrderPulse.Domain.Interfaces;
using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Infrastructure.Repositories;

public class ReturnRepository : IReturnRepository
{
    private readonly OrderPulseDbContext _db;

    public ReturnRepository(OrderPulseDbContext db)
    {
        _db = db;
    }

    public async Task<Return?> GetByIdAsync(Guid returnId, CancellationToken ct = default)
    {
        return await _db.Returns
            .Include(r => r.Order).ThenInclude(o => o.Retailer)
            .Include(r => r.Lines).ThenInclude(l => l.OrderLine)
            .Include(r => r.Refund)
            .FirstOrDefaultAsync(r => r.ReturnId == returnId, ct);
    }

    public async Task<(IReadOnlyList<Return> Items, int TotalCount)> GetReturnsAsync(
        ReturnQueryParameters query, CancellationToken ct = default)
    {
        var q = _db.Returns
            .Include(r => r.Order).ThenInclude(o => o.Retailer)
            .Include(r => r.Lines).ThenInclude(l => l.OrderLine)
            .AsQueryable();

        if (query.Status.HasValue)
        {
            q = q.Where(r => r.Status == query.Status.Value);
        }

        if (query.OrderId.HasValue)
        {
            q = q.Where(r => r.OrderId == query.OrderId.Value);
        }

        var totalCount = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(r => r.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<IReadOnlyList<Return>> GetOpenReturnLabelsAsync(CancellationToken ct = default)
    {
        return await _db.Returns
            .Include(r => r.Order).ThenInclude(o => o.Retailer)
            .Include(r => r.Lines).ThenInclude(l => l.OrderLine)
            .Where(r => r.Status == ReturnStatus.LabelIssued && r.ReturnByDate != null)
            .OrderBy(r => r.ReturnByDate)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Return>> GetAwaitingRefundAsync(CancellationToken ct = default)
    {
        return await _db.Returns
            .Include(r => r.Order).ThenInclude(o => o.Retailer)
            .Where(r => r.Status == ReturnStatus.Received || r.Status == ReturnStatus.RefundPending)
            .OrderBy(r => r.ReceivedByRetailerDate)
            .ToListAsync(ct);
    }

    public async Task<Return> CreateAsync(Return returnEntity, CancellationToken ct = default)
    {
        _db.Returns.Add(returnEntity);
        await _db.SaveChangesAsync(ct);
        return returnEntity;
    }

    public async Task UpdateAsync(Return returnEntity, CancellationToken ct = default)
    {
        returnEntity.UpdatedAt = DateTime.UtcNow;
        _db.Returns.Update(returnEntity);
        await _db.SaveChangesAsync(ct);
    }
}
