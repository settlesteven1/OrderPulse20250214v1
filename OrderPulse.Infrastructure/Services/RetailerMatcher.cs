using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderPulse.Domain.Entities;
using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Infrastructure.Services;

/// <summary>
/// Matches email sender addresses to known retailers using domain patterns.
/// Cached in memory for performance.
/// </summary>
public class RetailerMatcher
{
    private readonly OrderPulseDbContext _db;
    private List<RetailerPattern>? _patterns;

    public RetailerMatcher(OrderPulseDbContext db)
    {
        _db = db;
    }

    public async Task<Retailer?> MatchAsync(string fromAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fromAddress))
            return null;

        var domain = fromAddress.Split('@').LastOrDefault()?.ToLowerInvariant();
        if (domain is null)
            return null;

        var patterns = await GetPatternsAsync(ct);

        // Try exact domain match first
        var match = patterns.FirstOrDefault(p => p.Domains.Contains(domain));
        if (match is not null)
            return match.Retailer;

        // Try subdomain match (e.g., "email.amazon.com" matches "amazon.com")
        match = patterns.FirstOrDefault(p =>
            p.Domains.Any(d => domain.EndsWith("." + d)));
        if (match is not null)
            return match.Retailer;

        return null;
    }

    private async Task<List<RetailerPattern>> GetPatternsAsync(CancellationToken ct)
    {
        if (_patterns is not null)
            return _patterns;

        var retailers = await _db.Retailers.ToListAsync(ct);
        _patterns = retailers.Select(r => new RetailerPattern
        {
            Retailer = r,
            Domains = ParseDomains(r.SenderDomains)
        }).ToList();

        return _patterns;
    }

    private static HashSet<string> ParseDomains(string senderDomainsJson)
    {
        try
        {
            var domains = JsonSerializer.Deserialize<string[]>(senderDomainsJson);
            return domains?.Select(d => d.ToLowerInvariant()).ToHashSet()
                   ?? new HashSet<string>();
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    /// <summary>
    /// Clears the cached patterns. Call when retailers are updated.
    /// </summary>
    public void InvalidateCache() => _patterns = null;

    private class RetailerPattern
    {
        public Retailer Retailer { get; init; } = null!;
        public HashSet<string> Domains { get; init; } = new();
    }
}
