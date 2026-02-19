using System.Text.RegularExpressions;

namespace OrderPulse.Infrastructure.Services;

/// <summary>
/// Strips forwarding headers and preamble from forwarded email bodies
/// to help AI parsers focus on the original email content.
/// </summary>
public static partial class ForwardedEmailHelper
{
    /// <summary>
    /// Maximum body length to send to AI parsers. Bodies exceeding this
    /// are truncated after HTML stripping and forwarding-header removal.
    /// Increased from 20K to 30K to accommodate product line-item data
    /// that appears deep in Amazon's HTML structure.
    /// </summary>
    private const int MaxBodyLength = 30_000;

    // ── Forwarding header patterns ──

    // Gmail: "---------- Forwarded message ---------"
    [GeneratedRegex(@"-{5,}\s*Forwarded message\s*-{5,}", RegexOptions.IgnoreCase)]
    private static partial Regex GmailForwardMarker();

    // Outlook: "-----Original Message-----"
    [GeneratedRegex(@"-{5,}\s*Original Message\s*-{5,}", RegexOptions.IgnoreCase)]
    private static partial Regex OutlookForwardMarker();

    // Apple Mail: "Begin forwarded message:"
    [GeneratedRegex(@"Begin forwarded message\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex AppleForwardMarker();

    // Subject line "Fwd:" / "Fw:" prefix (for cleaning subject)
    [GeneratedRegex(@"^\s*(Fwd?|Fw)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectFwdPrefix();

    // Forwarding metadata block: From/Date/Subject/To lines that follow a forward marker
    [GeneratedRegex(
        @"(?:From\s*:.*\n)?(?:Date\s*:.*\n)?(?:Subject\s*:.*\n)?(?:To\s*:.*\n)?(?:Cc\s*:.*\n)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ForwardMetadataBlock();

    // HTML forwarding dividers (Gmail/Outlook style)
    [GeneratedRegex(
        @"<div\s+class=""gmail_quote"">.*?(?=<div\s+class=""gmail_quote_attribution"">|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlGmailQuote();

    // ── HTML stripping patterns ──

    // <style> blocks (often 5-15KB of CSS in Amazon emails)
    [GeneratedRegex(@"<style[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleBlocks();

    // <script> blocks
    [GeneratedRegex(@"<script[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlocks();

    // HTML comments (<!-- ... -->), including conditional comments
    [GeneratedRegex(@"<!--[\s\S]*?-->")]
    private static partial Regex HtmlComments();

    // Tracking pixels: <img> tags with width/height of 0 or 1
    [GeneratedRegex(
        @"<img\s[^>]*(?:width\s*=\s*[""']?[01]|height\s*=\s*[""']?[01])[^>]*/?\s*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex TrackingPixels();

    // Runs of whitespace (spaces, tabs, newlines) — collapse to single space
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex ExcessiveSpaces();

    // Collapse 3+ consecutive newlines to 2
    [GeneratedRegex(@"(\r?\n){3,}")]
    private static partial Regex ExcessiveNewlines();

    /// <summary>
    /// Determines whether the subject line indicates a forwarded email.
    /// </summary>
    public static bool IsForwardedSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        return SubjectFwdPrefix().IsMatch(subject);
    }

    /// <summary>
    /// Strips "Fwd:" / "Fw:" prefix from a subject line.
    /// </summary>
    public static string CleanSubject(string subject)
    {
        return SubjectFwdPrefix().Replace(subject, "").Trim();
    }

    /// <summary>
    /// Pre-processes an email body to extract the original forwarded content,
    /// stripping forwarding headers, preamble, and HTML bloat (CSS, scripts,
    /// comments, tracking pixels). This preserves the actual visible content
    /// (product names, prices, order details) that AI parsers need.
    /// </summary>
    public static string ExtractOriginalBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        var cleaned = body;

        // Try each forwarding marker pattern; take the content AFTER the marker + metadata
        cleaned = TryStripForwardingPreamble(cleaned, GmailForwardMarker())
               ?? TryStripForwardingPreamble(cleaned, OutlookForwardMarker())
               ?? TryStripForwardingPreamble(cleaned, AppleForwardMarker())
               ?? cleaned;

        // Strip HTML bloat before truncating — this preserves the actual content
        // (product names, prices, etc.) that would otherwise be cut off
        cleaned = StripHtmlBloat(cleaned);

        // Truncate if still too long
        if (cleaned.Length > MaxBodyLength)
            cleaned = cleaned[..MaxBodyLength];

        return cleaned;
    }

    /// <summary>
    /// Removes non-content HTML elements that consume space without carrying
    /// useful data for AI parsers: CSS, scripts, comments, tracking pixels,
    /// and excessive whitespace. Typically reduces Amazon HTML emails from
    /// 70-90KB to 15-25KB while preserving all visible text.
    /// </summary>
    private static string StripHtmlBloat(string html)
    {
        var result = html;

        // Remove <style> blocks — often 5-15KB of CSS in retailer emails
        result = StyleBlocks().Replace(result, "");

        // Remove <script> blocks
        result = ScriptBlocks().Replace(result, "");

        // Remove HTML comments (including conditional IE comments)
        result = HtmlComments().Replace(result, "");

        // Remove tracking pixels (1x1 or 0x0 images)
        result = TrackingPixels().Replace(result, "");

        // Collapse excessive whitespace
        result = ExcessiveSpaces().Replace(result, " ");
        result = ExcessiveNewlines().Replace(result, "\n\n");

        return result.Trim();
    }

    private static string? TryStripForwardingPreamble(string body, Regex markerPattern)
    {
        var match = markerPattern.Match(body);
        if (!match.Success)
            return null;

        // Take everything after the forwarding marker
        var afterMarker = body[(match.Index + match.Length)..];

        // Strip the metadata block (From:/Date:/Subject:/To: lines) that typically follows
        afterMarker = ForwardMetadataBlock().Replace(afterMarker, "", 1);

        return afterMarker.TrimStart('\r', '\n', ' ');
    }
}
