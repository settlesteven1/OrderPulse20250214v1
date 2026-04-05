using System.Text.RegularExpressions;

namespace OrderPulse.Infrastructure.Services;

/// <summary>
/// Strips forwarding headers, HTML markup, invisible Unicode characters, and email
/// boilerplate from email bodies so AI parsers receive clean, concise text.
///
/// Amazon HTML emails are typically 60-80K chars of raw HTML. This helper converts
/// them to ~2-5K chars of plain text, keeping delivery data, order references,
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

    // ── QR code extraction patterns ──
    // Matches <img> tags whose src contains a base64 data URI (any image type)
    [GeneratedRegex(
        @"<img\s[^>]*src\s*=\s*[""'](data:image/[^;]+;base64,[A-Za-z0-9+/=\s]+)[""'][^>]*/?\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Base64ImageTags();

    // Matches <img> tags that look like QR codes based on alt, title, class, or id attributes
    [GeneratedRegex(
        @"<img\s[^>]*(?:alt|title|class|id)\s*=\s*[""'][^""']*(?:qr|barcode|scan)[^""']*[""'][^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex QrCodeImageByAttribute();

    // Extracts the src attribute value from an <img> tag
    [GeneratedRegex(
        @"src\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrcAttribute();

    // Matches <img> tags whose src URL contains QR-related path segments (e.g. Amazon CDN QR codes)
    [GeneratedRegex(
        @"<img\s[^>]*src\s*=\s*[""'][^""']*(?:qr|barcode|qrcode)[^""']*[""'][^>]*/?\s*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex QrCodeImageBySrcUrl();

    // Matches <a> or <img> tags near QR-related text context (within a table cell or div)
    [GeneratedRegex(
        @"<(?:td|div|span)[^>]*>[\s\S]{0,200}?(?:qr\s*code|scan\s*(?:this|the|your|at))[\s\S]{0,500}?<img\s[^>]*src\s*=\s*[""']([^""']+)[""'][^>]*/?\s*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex QrCodeImageByProximity();

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

    // ── Unicode zero-width / invisible character pattern ──
    // Amazon emails embed these heavily for tracking and anti-scraping.
    // U+200B  Zero-width space
    // U+200C  Zero-width non-joiner
    // U+200D  Zero-width joiner
    // U+200E  Left-to-right mark
    // U+200F  Right-to-left mark
    // U+034F  Combining grapheme joiner
    // U+00AD  Soft hyphen
    // U+FEFF  Byte order mark / zero-width no-break space
    // U+2060  Word joiner
    // U+2061-U+2064  Invisible math operators
    // U+202A-U+202E  Bidi control characters (includes RTL/LTR embedding)
    // U+2066-U+2069  Bidi isolate characters
    // U+FE00-U+FE0F  Variation selectors
    [GeneratedRegex(@"[\u200B-\u200F\u034F\u00AD\uFEFF\u2060-\u2064\u202A-\u202E\u2066-\u2069\uFE00-\uFE0F]")]
    private static partial Regex InvisibleUnicodeChars();

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
    /// 3. Strips invisible Unicode characters (zero-width spaces, bidi marks, etc.)
    /// 4. Truncates to MaxBodyLength if still too long
    ///
    /// This typically reduces Amazon HTML emails from 60-80K to 2-5K chars
    /// of clean text, ensuring delivery data, tracking numbers, and order
    /// references are preserved rather than being buried in invisible chars.
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

        // Convert HTML to plain text
        cleaned = ConvertHtmlToText(cleaned);

        // Strip invisible Unicode characters (zero-width spaces, bidi marks, etc.)
        // This is critical for Amazon emails which embed thousands of these characters.
        cleaned = InvisibleUnicodeChars().Replace(cleaned, "");

        // Collapse whitespace again after Unicode removal (may have left gaps)
        cleaned = ExcessiveSpaces().Replace(cleaned, " ");
        cleaned = ExcessiveNewlines().Replace(cleaned, "\n\n");

        // Truncate if still too long
        if (cleaned.Length > MaxBodyLength)
            cleaned = cleaned[..MaxBodyLength];

        return cleaned.Trim();
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

    /// <summary>
    /// Extracts QR code image data from raw email HTML.
    ///
    /// Strategy (ordered by confidence):
    /// 1. Look for &lt;img&gt; tags with QR-related alt/title/class/id attributes
    /// 2. Look for &lt;img&gt; tags whose src URL contains "qr" or "barcode" (Amazon CDN, etc.)
    /// 3. Look for &lt;img&gt; tags near QR-related text ("QR code", "Scan this", etc.)
    /// 4. Scan base64 data URI images for QR-sized images with contextual hints
    /// 5. Fall back to medium-sized base64 images in the QR size range
    ///
    /// Returns a data URI (data:image/png;base64,...), image URL, or null if no QR found.
    /// </summary>
    public static string? ExtractQrCodeImageData(string rawHtml)
    {
        if (string.IsNullOrWhiteSpace(rawHtml))
            return null;

        // Strategy 1: <img> tags with QR-related alt/title/class/id attributes
        var qrImgMatches = QrCodeImageByAttribute().Matches(rawHtml);
        foreach (Match qrImg in qrImgMatches)
        {
            var srcMatch = ImgSrcAttribute().Match(qrImg.Value);
            if (srcMatch.Success)
            {
                var src = srcMatch.Groups[1].Value.Trim();
                if (!IsTrackingPixel(qrImg.Value))
                    return src;
            }
        }

        // Strategy 2: <img> tags whose src URL contains "qr" or "barcode" (e.g. Amazon CDN)
        var qrSrcMatches = QrCodeImageBySrcUrl().Matches(rawHtml);
        foreach (Match qrSrc in qrSrcMatches)
        {
            var srcMatch = ImgSrcAttribute().Match(qrSrc.Value);
            if (srcMatch.Success)
            {
                var src = srcMatch.Groups[1].Value.Trim();
                if (!IsTrackingPixel(qrSrc.Value))
                    return src;
            }
        }

        // Strategy 3: <img> tags near QR-related text context ("QR code", "Scan at", etc.)
        var proximityMatches = QrCodeImageByProximity().Matches(rawHtml);
        foreach (Match proxMatch in proximityMatches)
        {
            var src = proxMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(src))
                return src;
        }

        // Strategy 4: Base64 data URI images with QR-related context
        var base64Matches = Base64ImageTags().Matches(rawHtml);
        foreach (Match b64Img in base64Matches)
        {
            var dataUri = b64Img.Groups[1].Value.Trim();
            if (dataUri.Length < 200 || dataUri.Length > 30_000)
                continue;

            var fullTag = b64Img.Value.ToLowerInvariant();
            if (fullTag.Contains("qr") || fullTag.Contains("barcode") || fullTag.Contains("scan"))
                return dataUri;
        }

        // Strategy 5: Medium-sized base64 images in the QR size range
        foreach (Match b64Img in base64Matches)
        {
            var dataUri = b64Img.Groups[1].Value.Trim();
            if (dataUri.Length >= 500 && dataUri.Length <= 15_000)
                return dataUri;
        }

        return null;
    }

    /// <summary>
    /// Checks if an img tag is likely a tracking pixel based on dimensions.
    /// </summary>
    private static bool IsTrackingPixel(string imgTag)
    {
        return TrackingPixels().IsMatch(imgTag);
    }
}
