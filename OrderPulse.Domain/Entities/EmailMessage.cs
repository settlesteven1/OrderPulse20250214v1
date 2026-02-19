using OrderPulse.Domain.Enums;

namespace OrderPulse.Domain.Entities;

public class EmailMessage
{
    public Guid EmailMessageId { get; set; }
    public Guid TenantId { get; set; }
    public string GraphMessageId { get; set; } = string.Empty;
    public string? InternetMessageId { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string? FromDisplayName { get; set; }
    public string? OriginalFromAddress { get; set; }
    public string Subject { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public string BodyBlobUrl { get; set; } = string.Empty;
    public string? BodyPreview { get; set; }
    public bool HasAttachments { get; set; }
    public EmailClassificationType? ClassificationType { get; set; }
    public decimal? ClassificationConfidence { get; set; }
    public ProcessingStatus ProcessingStatus { get; set; } = ProcessingStatus.Pending;
    public DateTime? ProcessedAt { get; set; }
    public string? ErrorDetails { get; set; }
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}
