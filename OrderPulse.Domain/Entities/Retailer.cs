namespace OrderPulse.Domain.Entities;

public class Retailer
{
    public Guid RetailerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string SenderDomains { get; set; } = "[]"; // JSON array
    public string? SenderPatterns { get; set; }         // JSON array of regex patterns
    public string? LogoUrl { get; set; }
    public string? WebsiteUrl { get; set; }
    public int? ReturnPolicyDays { get; set; }
    public string? ReturnPolicyNotes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
