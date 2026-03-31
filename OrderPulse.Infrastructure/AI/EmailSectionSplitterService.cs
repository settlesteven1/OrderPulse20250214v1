using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Enums;

namespace OrderPulse.Infrastructure.AI;

/// <summary>
/// Preprocesses email bodies to detect and split multi-order emails into per-order sections.
/// Uses a regex heuristic to avoid unnecessary AI calls for single-order emails,
/// then GPT-4o-mini to perform the actual split when multiple orders are detected.
/// </summary>
public class EmailSectionSplitterService
{
    private readonly AzureOpenAIService _ai;
    private readonly ILogger<EmailSectionSplitterService> _logger;
    private readonly Lazy<string> _systemPrompt;

    // Amazon: ###-#######-#######
    private static readonly Regex AmazonOrderPattern = new(
        @"\b\d{3}-\d{7}-\d{7}\b", RegexOptions.Compiled);

    // Best Buy: BBY01-XXXXXXX
    private static readonly Regex BestBuyOrderPattern = new(
        @"\bBBY01-\d{7}\b", RegexOptions.Compiled);

    /// <summary>
    /// Classification types that can contain multi-order content.
    /// Promotional emails, returns tied to a single order, etc. are excluded.
    /// </summary>
    private static readonly HashSet<EmailClassificationType> SplittableTypes = new()
    {
        EmailClassificationType.OrderConfirmation,
        EmailClassificationType.OrderModification,
        EmailClassificationType.ShipmentConfirmation,
        EmailClassificationType.ShipmentUpdate,
        EmailClassificationType.DeliveryConfirmation,
        EmailClassificationType.DeliveryIssue
    };

    public EmailSectionSplitterService(AzureOpenAIService ai, ILogger<EmailSectionSplitterService> logger)
    {
        _ai = ai;
        _logger = logger;
        _systemPrompt = new Lazy<string>(() =>
            AzureOpenAIService.LoadPrompt("EmailSectionSplitterPrompt.md"));
    }

    /// <summary>
    /// Analyzes an email body and splits it into per-order sections if multiple orders are detected.
    /// Returns a single section wrapping the original body for single-order emails (no AI call).
    /// </summary>
    public async Task<List<EmailSection>> SplitAsync(
        string subject,
        string body,
        EmailClassificationType? classificationType,
        CancellationToken ct = default)
    {
        // Early exit: non-splittable classification types
        if (classificationType is null || !SplittableTypes.Contains(classificationType.Value))
        {
            _logger.LogDebug("Skipping split for non-splittable type: {Type}", classificationType);
            return WrapOriginal(body);
        }

        // Heuristic: count distinct order numbers in the body
        var orderNumbers = DetectDistinctOrderNumbers(body);

        if (orderNumbers.Count <= 1)
        {
            _logger.LogDebug(
                "Heuristic detected {Count} order number(s) — skipping AI split",
                orderNumbers.Count);
            return WrapOriginal(body, orderNumbers);
        }

        // Multiple orders detected → call GPT-4o-mini to split
        _logger.LogInformation(
            "Detected {Count} distinct order numbers — invoking AI splitter: [{Orders}]",
            orderNumbers.Count, string.Join(", ", orderNumbers));

        try
        {
            var userPrompt = $"Subject: {subject}\n\nEmail Body:\n{body}";
            var response = await _ai.ClassifierCompleteAsync(
                _systemPrompt.Value, userPrompt, jsonMode: true, ct);

            var result = _ai.DeserializeResponse<EmailSectionSplitResult>(response);

            if (result?.Sections is { Count: > 0 })
            {
                _logger.LogInformation(
                    "AI splitter returned {Count} sections (was_split: {WasSplit})",
                    result.Sections.Count, result.WasSplit);
                return result.Sections;
            }

            _logger.LogWarning("AI splitter returned empty/null result — falling back to unsplit body");
        }
        catch (ContentFilterException)
        {
            _logger.LogWarning("Content filter triggered during split — falling back to unsplit body");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI splitter failed — falling back to unsplit body");
        }

        // Fallback: return original body as a single section
        return WrapOriginal(body, orderNumbers);
    }

    /// <summary>
    /// Uses regex patterns to find distinct order numbers in the email body.
    /// Returns a deduplicated list of order number strings.
    /// </summary>
    private static List<string> DetectDistinctOrderNumbers(string body)
    {
        var orderNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in AmazonOrderPattern.Matches(body))
            orderNumbers.Add(m.Value);

        foreach (Match m in BestBuyOrderPattern.Matches(body))
            orderNumbers.Add(m.Value);

        return orderNumbers.ToList();
    }

    /// <summary>
    /// Wraps the original body in a single-element list for the passthrough case.
    /// </summary>
    private static List<EmailSection> WrapOriginal(string body, List<string>? orderRefs = null)
    {
        return new List<EmailSection>
        {
            new()
            {
                Body = body,
                DetectedOrderReferences = orderRefs ?? new List<string>(),
                SectionIndex = 0
            }
        };
    }
}
