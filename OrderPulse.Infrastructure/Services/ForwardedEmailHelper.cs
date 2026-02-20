using System.Text.RegularExpressions;

namespace OrderPulse.Infrastructure.Services;

/// <summary>
/// Strips forwarding headers, HTML markup, and email boilerplate from email bodies
/// so AI parsers receive clean, concise text with the actual content they need.
///
/// Amazon HTML emails are typically 60-80K chars of raw HTML. This helper converts
/// them to ~5-15K chars of plain text, keeping delivery data, order references,
/// tracking numbers, and product names that would otherwise be truncated.
/// </summary>
public static partial class ForwardedEmailHelper
{
    /// <summary>
    /// Maximum body length to send to AI parsers after all cleaning.
    /// After HTML-to-text conversion, most emails fit well under this limit.
    /// </summary>
    private const int MaxBodyLength = 30_000;

    // ── Forwarding header patterns ──

    [GeneratedRegex(@"-{5,}\s*Forwarded message\s*-{5,}", RegexOptions.IgnoreCase)]
    private static partial Regex GmailForwardMarker();

    [GeneratedRegex(@"-{5,}\s*Original Message\s*-{5,}", RegexOptions.IgnoreCase)]
    private static partial Regex OutlookForwardMarker();

    [GeneratedRegex(@"Begin forwarded message\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex AppleForwardMarker();

    [GeneratedRegex(@"^\s*(Fwd?|Fw)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectFwdPrefix();

    [GeneratedRegex(
        @"(?:From\s*:.*\n)?(?:Date\s*:.*\n)?(?:Subject\s*:.*\n)?(?:To\s*:.*\n)?(?:Cc\s*:.*\n)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ForwardMetadataBlock();

    // ── HTML removal patterns ──

    [GeneratedRegex(@"<style[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleBlocks();

    [GeneratedRegex(@"<script[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlocks();

    [GeneratedRegex(@"<!--[\s\S]*?-->")]
    private static partial Regex HtmlComments();

    [GeneratedRegex(
        @"<img\s[^>]*(?:width\s*=\s*[""']?[01]|height\s*=\s*[""']?[01])[^>]*/?\s*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex TrackingPixels();

    [GeneratedRegex(@"<br\s*/?\s*>|</?p\s*>|</?div\s*>|</?tr\s*>|</?li\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockLevelTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AllHtmlTags();

    [GeneratedRegex(@"&nbsp;", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlNbsp();

    [GeneratedRegex(@"&amp;", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAmp();

    [GeneratedRegex(@"&lt;", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlLt();

    [GeneratedRegex(@"&gt;", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlGt();

    [GeneratedRegex(@"&quot;", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlQuot();

    [GeneratedRegex(@"&#\d+;")]
    private static partial Regex HtmlNumericEntities();

    [GeneratedRegex(@"&\w+;")]
    private static partial Regex HtmlNamedEntities();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex ExcessiveSpaces();

    [GeneratedRegex(@"(\r?\n){3,}")]
    private static partial Regex ExcessiveNewlines();

    public static bool IsForwardedSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        return SubjectFwdPrefix().IsMatch(subject);
    }

    public static string CleanSubject(string subject)
    {
        return SubjectFwdPrefix().Replace(subject, "").Trim();
    }

    /// <summary>
    /// Pre-processes an email body for AI parsing:
    /// 1. Strips forwarding headers/preamble (Gmail, Outlook, Apple Mail)
    /// 2. Converts HTML to plain text (removes tags, decodes entities)
    /// 3. Truncates to MaxBodyLength if still too long
    ///
    /// This typically reduces Amazon HTML emails from 60-80K to 5-15K chars
    /// of clean text, ensuring delivery data, tracking numbers, and order
    /// references are preserved rather than being cut off by truncation.
    /// </summary>
    public static string ExtractOriginalBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        var cleaned = body;

        // Strip forwarding preamble — take content AFTER the marker + metadata
        cleaned = TryStripForwardingPreamble(cleaned, GmailForwardMarker())
               ?? TryStripForwardingPreamble(cleaned, OutlookForwardMarker())
               ?? TryStripForwardingPreamble(cleaned, AppleForwardMarker())
               ?? cleaned;

        // Convert HTML to plain text — this is the key fix for issue #30.
        // Previous approach only stripped <style>/<script> but left all HTML tags,
        // resulting in 30K of HTML chrome with delivery data cut off.
        // Converting to plain text keeps the actual content within the limit.
        cleaned = ConvertHtmlToText(cleaned);

        // Truncate if still too long
        if (cleaned.Length > MaxBodyLength)
            cleaned = cleaned[..MaxBodyLength];

        return cleaned;
    }

    /// <summary>
    /// Converts HTML email body to plain text by:
    /// 1. Removing non-content elements (style, script, comments, tracking pixels)
    /// 2. Converting block-level tags to newlines (preserving visual structure)
    /// 3. Stripping all remaining HTML tags
    /// 4. Decoding HTML entities
    /// 5. Collapsing excessive whitespace
    /// </summary>
    private static string ConvertHtmlToText(string html)
    {
        var result = html;

        // Remove non-content blocks first (these can be huge)
        result = StyleBlocks().Replace(result, "");
        result = ScriptBlocks().Replace(result, "");
        result = HtmlComments().Replace(result, "");
        result = TrackingPixels().Replace(result, "");

        // Convert block-level tags to newlines so text doesn't run together
        result = BlockLevelTags().Replace(result, "\n");

        // Strip all remaining HTML tags
        result = AllHtmlTags().Replace(result, "");

        // Decode common HTML entities
        result = HtmlNbsp().Replace(result, " ");
        result = HtmlAmp().Replace(result, "&");
        result = HtmlLt().Replace(result, "<");
        result = HtmlGt().Replace(result, ">");
        result = HtmlQuot().Replace(result, "\"");
        result = HtmlNumericEntities().Replace(result, "");
        result = HtmlNamedEntities().Replace(result, "");

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

        // Strip the metadata block (From:/Date:/Subject:/To: lines) that follows
        afterMarker = ForwardMetadataBlock().Replace(afterMarker, "", 1);

        return afterMarker.TrimStart('\r', '\n', ' ');
    }
}
