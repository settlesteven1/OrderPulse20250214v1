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
    /// are truncated after forwarding-header stripping.
    /// </summary>
    private const int MaxBodyLength = 20_000;

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
    /// stripping forwarding headers and preamble. If the body is not a forwarded
    /// email, it is returned as-is (potentially truncated).
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

        // Truncate if still too long
        if (cleaned.Length > MaxBodyLength)
            cleaned = cleaned[..MaxBodyLength];

        return cleaned;
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
