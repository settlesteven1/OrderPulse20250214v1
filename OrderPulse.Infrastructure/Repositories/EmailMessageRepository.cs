using Microsoft.EntityFrameworkCore;
using OrderPulse.Domain.Entities;
using OrderPulse.Domain.Enums;
using OrderPulse.Domain.Interfaces;
using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Infrastructure.Repositories;

public class EmailMessageRepository : IEmailMessageRepository
{
    private readonly OrderPulseDbContext _db;

    public EmailMessageRepository(OrderPulseDbContext db)
    {
        _db = db;
    }

    public async Task<EmailMessage?> GetByGraphMessageIdAsync(string graphMessageId, CancellationToken ct = default)
    {
        return await _db.EmailMessages
            .FirstOrDefaultAsync(e => e.GraphMessageId == graphMessageId, ct);
    }

    public async Task<IReadOnlyList<EmailMessage>> GetPendingAsync(int batchSize = 50, CancellationToken ct = default)
    {
        return await _db.EmailMessages
            .Where(e => e.ProcessingStatus == ProcessingStatus.Pending)
            .OrderBy(e => e.ReceivedAt)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<EmailMessage> Items, int TotalCount)> GetReviewQueueAsync(
        int page, int pageSize, CancellationToken ct = default)
    {
        var q = _db.EmailMessages
            .Where(e => e.ProcessingStatus == ProcessingStatus.ManualReview);

        var totalCount = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(e => e.ReceivedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<EmailMessage> CreateAsync(EmailMessage email, CancellationToken ct = default)
    {
        _db.EmailMessages.Add(email);
        await _db.SaveChangesAsync(ct);
        return email;
    }

    public async Task UpdateAsync(EmailMessage email, CancellationToken ct = default)
    {
        _db.EmailMessages.Update(email);
        await _db.SaveChangesAsync(ct);
    }
}
