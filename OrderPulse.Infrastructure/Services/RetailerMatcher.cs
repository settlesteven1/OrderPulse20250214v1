using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderPulse.Domain.Entities;
using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Infrastructure.Services;

/// <summary>
/// Matches email sender addresses to known retailers using domain patterns.
/// Cached in memory for performance.
/// </summary>
public partial class RetailerMatcher
{
    private readonly OrderPulseDbContext _db;
    private List<RetailerPattern>? _patterns;

    /// <summary>Extracts email addresses from plain text or lightly-cleaned HTML.</summary>
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled)]
    private static partial Regex EmailAddressPattern();

    /// <summary>Common personal email domains to skip when scanning the body.</summary>
    private static readonly HashSet<string> PersonalDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "yahoo.com", "hotmail.com", "outlook.com", "live.com",
        "aol.com", "icloud.com", "me.com", "msn.com", "protonmail.com",
        "mail.com", "zoho.com", "yandex.com"
    };

    public RetailerMatcher(OrderPulseDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Matches an email sender to a known retailer by address only.
    /// </summary>
    public async Task<Retailer?> MatchAsync(string fromAddress, CancellationToken ct = default)
    {
        return await MatchAsync(fromAddress, originalFromAddress: null, ct);
    }

    /// <summary>
    /// Matches an email sender to a known retailer, trying the primary from address
    /// first, then falling back to the original sender address (for forwarded emails).
    /// </summary>
    public async Task<Retailer?> MatchAsync(string fromAddress, string? originalFromAddress, CancellationToken ct = default)
    {
        // Try the primary from address first
        var result = await MatchByAddressAsync(fromAddress, ct);
        if (result is not null)
            return result;

        // Fall back to original sender address (for forwarded emails)
        if (!string.IsNullOrWhiteSpace(originalFromAddress))
        {
            result = await MatchByAddressAsync(originalFromAddress, ct);
        }

        return result;
    }

    /// <summary>
    /// Last-resort fallback: scans the email body for all email addresses and tries
    /// to match each one against known retailer domains. Skips personal email domains
    /// (gmail, yahoo, etc.) to avoid false matches on the forwarding user's address.
    /// Returns a tuple of (matched retailer, the email address that matched) so the
    /// caller can backfill OriginalFromAddress.
    /// </summary>
    public async Task<(Retailer? Retailer, string? MatchedAddress)> MatchFromBodyAsync(
        string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);

        // Extract all email addresses from the body
        var matches = EmailAddressPattern().Matches(body);
        var candidates = matches
            .Select(m => m.Value.ToLowerInvariant())
            .Distinct()
            .Where(addr =>
            {
                var domain = addr.Split('@').LastOrDefault();
                // Skip personal email domains — they're the forwarding user, not the retailer
                return domain is not null && !PersonalDomains.Contains(domain);
            })
            .ToList();

        foreach (var candidate in candidates)
        {
            var retailer = await MatchByAddressAsync(candidate, ct);
            if (retailer is not null)
                return (retailer, candidate);
        }

        return (null, null);
    }

    private async Task<Retailer?> MatchByAddressAsync(string? address, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        var domain = address.Split('@').LastOrDefault()?.ToLowerInvariant();
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
