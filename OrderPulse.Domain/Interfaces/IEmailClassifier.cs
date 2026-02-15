using OrderPulse.Domain.Enums;

namespace OrderPulse.Domain.Interfaces;

/// <summary>
/// Classifies incoming emails into one of the 14 message types.
/// </summary>
public interface IEmailClassifier
{
    /// <summary>
    /// Quick pre-filter: determines if an email is order-related or noise.
    /// Uses GPT-4o-mini for cost efficiency.
    /// </summary>
    Task<bool> IsOrderRelatedAsync(string subject, string bodyPreview, string fromAddress, CancellationToken ct = default);

    /// <summary>
    /// Full classification: determines the exact message type.
    /// Uses GPT-4o for accuracy.
    /// </summary>
    Task<ClassificationResult> ClassifyAsync(string subject, string body, string fromAddress, CancellationToken ct = default);
}

public record ClassificationResult(
    EmailClassificationType Type,
    decimal Confidence,
    string? SecondaryType = null  // For multi-type emails
);

/// <summary>
/// Parses structured data from a classified email.
/// Each implementation handles a specific message type or group of types.
/// </summary>
public interface IEmailParser<TResult> where TResult : class
{
    Task<ParseResult<TResult>> ParseAsync(string subject, string body, string fromAddress, string? retailerContext, CancellationToken ct = default);
}

public record ParseResult<T>(
    T? Data,
    decimal Confidence,
    bool NeedsReview,
    string? ErrorMessage = null
) where T : class;

/// <summary>
/// Routes classified emails to the appropriate parser.
/// </summary>
public interface IEmailProcessingOrchestrator
{
    Task ProcessEmailAsync(Guid emailMessageId, CancellationToken ct = default);
}
