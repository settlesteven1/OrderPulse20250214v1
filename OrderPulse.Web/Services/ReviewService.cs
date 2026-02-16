using System.Net.Http.Json;

namespace OrderPulse.Web.Services;

public class ReviewService
{
    private readonly HttpClient _http;

    public ReviewService(HttpClient http) => _http = http;

    public async Task<(List<ReviewQueueItem> Items, PaginationMeta? Pagination)> GetQueueAsync(int page = 1, int pageSize = 20)
    {
        var url = $"api/review?page={page}&pageSize={pageSize}";
        var response = await _http.GetFromJsonAsync<ApiResponse<List<ReviewQueueItem>>>(url);
        return (response?.Data ?? new(), response?.Pagination);
    }

    public async Task<ReviewDetail?> GetDetailAsync(Guid emailMessageId)
    {
        var response = await _http.GetFromJsonAsync<ApiResponse<ReviewDetail>>(
            $"api/review/{emailMessageId}");
        return response?.Data;
    }

    public async Task ApproveAsync(Guid emailMessageId, ReviewCorrection? corrections = null)
    {
        await _http.PostAsJsonAsync($"api/review/{emailMessageId}/approve", corrections ?? new());
    }

    public async Task DismissAsync(Guid emailMessageId)
    {
        await _http.PostAsync($"api/review/{emailMessageId}/dismiss", null);
    }

    public async Task ReprocessAsync(Guid emailMessageId)
    {
        await _http.PostAsync($"api/review/{emailMessageId}/reprocess", null);
    }
}

// ── Review Queue DTOs ──

public class ReviewQueueItem
{
    public Guid EmailMessageId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string? FromDisplayName { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string? ClassificationType { get; set; }
    public decimal? ClassificationConfidence { get; set; }
    public string ProcessingStatus { get; set; } = string.Empty;
    public string? BodyPreview { get; set; }
}

public class ReviewDetail
{
    public Guid EmailMessageId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string? FromDisplayName { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string? ClassificationType { get; set; }
    public decimal? ClassificationConfidence { get; set; }
    public string ProcessingStatus { get; set; } = string.Empty;
    public string? BodyHtml { get; set; }
    public string? BodyPreview { get; set; }

    // AI-parsed output
    public string? ParsedOrderNumber { get; set; }
    public string? ParsedRetailer { get; set; }
    public decimal? ParsedAmount { get; set; }
    public string? ParsedCurrency { get; set; }
    public string? ParsedTrackingNumber { get; set; }
    public string? ParsedCarrier { get; set; }
    public string? ParsedRawJson { get; set; }
    public string? ErrorDetails { get; set; }
}

public class ReviewCorrection
{
    public string? CorrectedClassificationType { get; set; }
    public string? CorrectedOrderNumber { get; set; }
    public string? CorrectedRetailer { get; set; }
    public decimal? CorrectedAmount { get; set; }
    public string? CorrectedTrackingNumber { get; set; }
    public string? CorrectedCarrier { get; set; }
}
