namespace OrderPulse.Infrastructure.AI;

/// <summary>
/// Result of splitting a multi-order email into per-order sections.
/// If the email contains only one order (the common case), Sections will have a single entry.
/// </summary>
public class EmailSectionSplitResult
{
    public List<EmailSection> Sections { get; set; } = new();
    public int DistinctOrderCount { get; set; }
    public bool WasSplit { get; set; }
    public decimal Confidence { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// A single order-scoped section extracted from a multi-order email.
/// Contains the relevant body text and any detected order references.
/// </summary>
public class EmailSection
{
    /// <summary>
    /// The extracted section text — ready for the downstream parser.
    /// For single-order emails, this is the full original body.
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Order numbers detected in this section (e.g., "112-4271087-1813067").
    /// Helps the orchestrator hint at which order to link, even if the parser
    /// would otherwise miss the reference.
    /// </summary>
    public List<string> DetectedOrderReferences { get; set; } = new();

    /// <summary>
    /// Sequential position in the original email (0-based).
    /// </summary>
    public int SectionIndex { get; set; }
}
