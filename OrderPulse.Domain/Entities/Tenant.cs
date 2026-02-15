using OrderPulse.Domain.Enums;

namespace OrderPulse.Domain.Entities;

public class Tenant
{
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PurchaseMailbox { get; set; } = string.Empty;
    public MailboxProvider MailboxProvider { get; set; }
    public string? GraphRefreshToken { get; set; }
    public string? GraphSubscriptionId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSyncAt { get; set; }

    // Navigation
    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<EmailMessage> EmailMessages { get; set; } = new List<EmailMessage>();
}
