namespace OrderPulse.Domain.Enums;

public enum ProcessingStatus
{
    Pending,
    Classifying,
    Classified,
    Parsing,
    Parsed,
    Failed,
    ManualReview,
    Dismissed
}
