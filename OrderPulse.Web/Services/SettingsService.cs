using System.Net.Http.Json;

namespace OrderPulse.Web.Services;

public class SettingsService
{
    private readonly HttpClient _http;

    public SettingsService(HttpClient http) => _http = http;

    public async Task<TenantSettings?> GetSettingsAsync()
    {
        var response = await _http.GetFromJsonAsync<ApiResponse<TenantSettings>>("api/settings");
        return response?.Data;
    }

    public async Task SaveSettingsAsync(TenantSettingsUpdate update)
    {
        await _http.PutAsJsonAsync("api/settings", update);
    }

    public async Task<HistoricalImportResult?> TriggerHistoricalImportAsync(HistoricalImportRequest request)
    {
        var response = await _http.PostAsJsonAsync("api/settings/import", request);
        return await response.Content.ReadFromJsonAsync<HistoricalImportResult>();
    }
}

// ── Settings DTOs ──

public class TenantSettings
{
    // Mailbox
    public string? ConnectedEmail { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string MailboxStatus { get; set; } = "Unknown";

    // Sync
    public int PollingIntervalMinutes { get; set; } = 5;
    public bool WebhookEnabled { get; set; }

    // Notifications
    public bool NotifyDelivery { get; set; } = true;
    public bool NotifyShipment { get; set; } = true;
    public bool NotifyReturn { get; set; } = true;
    public bool NotifyRefund { get; set; } = true;
    public bool NotifyIssues { get; set; } = true;
}

public class TenantSettingsUpdate
{
    public int PollingIntervalMinutes { get; set; }
    public bool WebhookEnabled { get; set; }
    public bool NotifyDelivery { get; set; }
    public bool NotifyShipment { get; set; }
    public bool NotifyReturn { get; set; }
    public bool NotifyRefund { get; set; }
    public bool NotifyIssues { get; set; }
}

public class HistoricalImportRequest
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
}

public class HistoricalImportResult
{
    public int EmailsQueued { get; set; }
    public string? Message { get; set; }
}
