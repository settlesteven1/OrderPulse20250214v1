using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Entities;
using OrderPulse.Infrastructure.AI;
using OrderPulse.Infrastructure.Data;
using OrderPulse.Infrastructure.Services;

namespace OrderPulse.Functions.ItemNameResolution;

/// <summary>
/// Timer-triggered function that resolves truncated product names on order lines.
/// Runs daily at 9:00 AM UTC. For each order line with a truncated name,
/// extracts the product URL from the source email HTML, fetches the product page,
/// and uses GPT-4o-mini to extract the full product name.
/// </summary>
public partial class ItemNameResolutionFunction
{
    private readonly ILogger<ItemNameResolutionFunction> _logger;
    private readonly OrderPulseDbContext _db;
    private readonly AzureOpenAIService _ai;
    private readonly IHttpClientFactory _httpFactory;
    private readonly EmailBlobStorageService _blobService;
    private readonly ProcessingLogger _log;

    private static readonly Lazy<string> ProductNamePrompt = new(() =>
        AzureOpenAIService.LoadPrompt("ProductNamePrompt.md"));

    /// <summary>Maximum items to resolve per run (to control AI costs).</summary>
    private const int MaxItemsPerRun = 30;

    /// <summary>Maximum page content length to send to AI.</summary>
    private const int MaxPageContentLength = 15_000;

    /// <summary>Maximum number of resolution attempts before giving up on an item.</summary>
    private const int MaxAttempts = 3;

    public ItemNameResolutionFunction(
        ILogger<ItemNameResolutionFunction> logger,
        OrderPulseDbContext db,
        AzureOpenAIService ai,
        IHttpClientFactory httpFactory,
        EmailBlobStorageService blobService,
        ProcessingLogger log)
    {
        _logger = logger;
        _db = db;
        _ai = ai;
        _httpFactory = httpFactory;
        _blobService = blobService;
        _log = log;
    }

    [Function("ItemNameResolutionFunction")]
    public async Task Run(
        [TimerTrigger("0 0 9 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        _logger.LogInformation("Item name resolution started at {time}", DateTime.UtcNow);

        // Find order lines with truncated names (ending with "..." or "…")
        var truncatedItems = await _db.OrderLines
            .IgnoreQueryFilters()
            .Include(ol => ol.Order)
                .ThenInclude(o => o.SourceEmail)
            .Where(ol =>
                (ol.ProductName.EndsWith("...") || ol.ProductName.EndsWith("\u2026")) &&
                ol.Order.SourceEmail != null &&
                ol.Order.SourceEmail.BodyBlobUrl != null)
            .OrderBy(ol => ol.CreatedAt)
            .Take(MaxItemsPerRun)
            .ToListAsync(ct);

        _logger.LogInformation("Found {count} truncated item names to resolve", truncatedItems.Count);

        if (truncatedItems.Count == 0) return;

        // Group by source email to avoid fetching the same blob multiple times
        var byEmail = truncatedItems
            .GroupBy(ol => ol.Order.SourceEmail!.EmailMessageId)
            .ToList();

        var resolvedCount = 0;
        var failedCount = 0;

        foreach (var emailGroup in byEmail)
        {
            try
            {
                var sourceEmail = emailGroup.First().Order.SourceEmail!;
                var htmlBody = await _blobService.GetEmailBodyAsync(sourceEmail.BodyBlobUrl!, ct);

                if (string.IsNullOrWhiteSpace(htmlBody))
                {
                    _logger.LogWarning("Empty HTML body for email {id}", sourceEmail.EmailMessageId);
                    failedCount += emailGroup.Count();
                    continue;
                }

                // Extract all product URLs from the HTML
                var productLinks = ExtractProductLinks(htmlBody);

                _logger.LogInformation(
                    "Email {id}: found {linkCount} product links for {itemCount} truncated items",
                    sourceEmail.EmailMessageId, productLinks.Count, emailGroup.Count());

                foreach (var orderLine in emailGroup)
                {
                    try
                    {
                        var resolved = await ResolveItemNameAsync(orderLine, productLinks, ct);
                        if (resolved)
                            resolvedCount++;
                        else
                            failedCount++;
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        _logger.LogWarning(ex,
                            "Failed to resolve name for order line {id} ({name})",
                            orderLine.OrderLineId, orderLine.ProductName);
                    }

                    // Brief delay between requests
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            catch (Exception ex)
            {
                failedCount += emailGroup.Count();
                _logger.LogWarning(ex, "Failed to process email group {id}", emailGroup.Key);
            }
        }

        _logger.LogInformation(
            "Item name resolution complete: {total} checked, {resolved} resolved, {failed} failed",
            truncatedItems.Count, resolvedCount, failedCount);
    }

    /// <summary>
    /// Attempts to resolve a truncated product name by finding its product URL
    /// in the source email HTML, fetching the product page, and extracting the full name.
    /// </summary>
    private async Task<bool> ResolveItemNameAsync(
        OrderLine orderLine, List<ProductLink> productLinks, CancellationToken ct)
    {
        var truncatedName = orderLine.ProductName.TrimEnd('.', '\u2026').Trim();

        // Find the best matching product URL for this truncated name
        var bestMatch = FindBestMatchingLink(truncatedName, productLinks);

        if (bestMatch is null)
        {
            _logger.LogDebug("No product URL found for: {name}", orderLine.ProductName);
            return false;
        }

        _logger.LogInformation("Matched '{name}' to URL: {url}", orderLine.ProductName, bestMatch.Url);

        // Fetch the product page
        var pageText = await FetchProductPageAsync(bestMatch.Url, ct);
        if (string.IsNullOrWhiteSpace(pageText) || pageText.Length < 50)
        {
            _logger.LogDebug("Product page returned insufficient content for: {url}", bestMatch.Url);
            return false;
        }

        // Ask GPT-4o-mini to extract the full product name
        var userPrompt = $"Truncated Name: {orderLine.ProductName}\n\nPage Content:\n{pageText}";
        var aiResponse = await _ai.ClassifierCompleteAsync(
            ProductNamePrompt.Value, userPrompt, jsonMode: true, ct);

        var result = _ai.DeserializeResponse<ProductNameResponse>(aiResponse);
        if (result is null || result.Status != "Success" || string.IsNullOrWhiteSpace(result.FullProductName))
        {
            _logger.LogDebug("AI could not extract full name for: {name}", orderLine.ProductName);
            return false;
        }

        // Update the order line
        var oldName = orderLine.ProductName;
        orderLine.ProductName = result.FullProductName;
        orderLine.ProductUrl ??= bestMatch.Url;
        orderLine.UpdatedAt = DateTime.UtcNow;

        // Set tenant context for RLS
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"EXEC sp_set_session_context @key=N'TenantId', @value={orderLine.Order.TenantId.ToString()}", ct);

        await _db.SaveChangesAsync(ct);

        await _log.Success(Guid.Empty, "ItemNameResolution",
            $"Resolved truncated name: '{oldName}' -> '{result.FullProductName}'");

        _logger.LogInformation(
            "Resolved item name: '{old}' -> '{new}' (line: {id})",
            oldName, result.FullProductName, orderLine.OrderLineId);

        return true;
    }

    /// <summary>
    /// Extracts product links from HTML email body.
    /// Finds anchor tags with product URLs and their associated text.
    /// Supports Amazon, Walmart, Best Buy, and other major retailers.
    /// </summary>
    internal static List<ProductLink> ExtractProductLinks(string html)
    {
        var links = new List<ProductLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Match <a> tags with href containing product page patterns
        var anchorMatches = AnchorTagRegex().Matches(html);

        foreach (Match match in anchorMatches)
        {
            var href = match.Groups["href"].Value;
            var innerText = StripHtmlTags(match.Groups["inner"].Value).Trim();

            // Skip empty or very short link text
            if (string.IsNullOrWhiteSpace(innerText) || innerText.Length < 5) continue;

            // Normalize Amazon redirect URLs
            var url = NormalizeProductUrl(href);
            if (url is null) continue;

            // Skip if we've already seen this URL
            if (!seen.Add(url)) continue;

            links.Add(new ProductLink(url, innerText));
        }

        return links;
    }

    /// <summary>
    /// Normalizes product URLs, particularly Amazon redirect URLs that contain
    /// the actual product URL encoded within them.
    /// </summary>
    private static string? NormalizeProductUrl(string href)
    {
        // Amazon emails use redirect URLs like:
        // https://www.amazon.com/gp/r.html?...&url=https%3A%2F%2Fwww.amazon.com%2Fdp%2FB0XXXXX...
        if (href.Contains("amazon.com/gp/r.html", StringComparison.OrdinalIgnoreCase) ||
            href.Contains("amazon.com/gp/redirect", StringComparison.OrdinalIgnoreCase))
        {
            // Try to extract the actual product URL from the redirect
            var urlMatch = RedirectUrlParam().Match(href);
            if (urlMatch.Success)
            {
                var decoded = Uri.UnescapeDataString(urlMatch.Groups["target"].Value);
                if (IsProductUrl(decoded))
                    return decoded;
            }
        }

        // Direct product URL patterns
        if (IsProductUrl(href))
            return href;

        return null;
    }

    /// <summary>
    /// Checks if a URL matches known product page patterns.
    /// </summary>
    private static bool IsProductUrl(string url)
    {
        // Amazon: /dp/, /gp/product/, /gp/aw/d/
        if (AmazonProductUrlRegex().IsMatch(url)) return true;

        // Walmart: /ip/
        if (url.Contains("walmart.com", StringComparison.OrdinalIgnoreCase) &&
            url.Contains("/ip/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Best Buy: /site/.../.p
        if (url.Contains("bestbuy.com/site/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Generic product patterns
        if (url.Contains("/product/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/item/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Finds the product link that best matches a truncated product name.
    /// Uses prefix matching since the truncated name is the start of the full name.
    /// </summary>
    private static ProductLink? FindBestMatchingLink(string truncatedName, List<ProductLink> links)
    {
        if (links.Count == 0) return null;

        // Clean up the truncated name for matching
        var searchName = truncatedName.ToLowerInvariant().Trim();

        // First pass: look for links whose text starts with the truncated name
        foreach (var link in links)
        {
            var linkText = link.Text.ToLowerInvariant();
            if (linkText.StartsWith(searchName) || linkText.Contains(searchName))
                return link;
        }

        // Second pass: try matching first few significant words
        var words = searchName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
        {
            // Match on first 3 words (brand + model typically)
            var keyWords = words.Take(Math.Min(3, words.Length)).ToArray();

            ProductLink? bestMatch = null;
            var bestScore = 0;

            foreach (var link in links)
            {
                var linkText = link.Text.ToLowerInvariant();
                var score = keyWords.Count(w => linkText.Contains(w));

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = link;
                }
            }

            if (bestMatch is not null && bestScore >= 2)
                return bestMatch;
        }

        return null;
    }

    /// <summary>
    /// Fetches a product page and returns its text content.
    /// Uses a browser-like User-Agent and truncates for AI cost control.
    /// </summary>
    private async Task<string?> FetchProductPageAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("TrackingClient");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("text/html"));
            client.Timeout = TimeSpan.FromSeconds(15);

            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Product page returned {status} for {url}",
                    response.StatusCode, url);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(ct);

            // Convert HTML to plain text
            var text = ForwardedEmailHelper.ExtractOriginalBody(html);

            // Truncate to keep AI costs reasonable
            return text.Length > MaxPageContentLength ? text[..MaxPageContentLength] : text;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch product page: {url}", url);
            return null;
        }
    }

    /// <summary>Strips HTML tags from a string, returning plain text.</summary>
    private static string StripHtmlTags(string html)
    {
        return HtmlTagRegex().Replace(html, " ").Trim();
    }

    // ── Regex patterns ──

    [GeneratedRegex(
        @"<a\s[^>]*href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<inner>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorTagRegex();

    [GeneratedRegex(
        @"[?&]url=(?<target>[^&]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex RedirectUrlParam();

    [GeneratedRegex(
        @"amazon\.com/(?:dp|gp/product|gp/aw/d)/[A-Z0-9]{10}",
        RegexOptions.IgnoreCase)]
    private static partial Regex AmazonProductUrlRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    // ── Inner types ──

    internal record ProductLink(string Url, string Text);

    private record ProductNameResponse(string Status, string? FullProductName);
}
