using System.Net.Http.Json;

namespace OrderPulse.Web.Services;

public class ReturnService
{
    private readonly HttpClient _http;

    public ReturnService(HttpClient http)
    {
        _http = http;
    }

    public async Task<(List<ReturnCardDto> Items, PaginationMeta? Pagination)> GetReturnsAsync(
        string? status = null, int page = 1, int pageSize = 50)
    {
        var queryParams = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrEmpty(status)) queryParams.Add($"status={status}");

        var url = $"api/returns?{string.Join("&", queryParams)}";
        var response = await _http.GetFromJsonAsync<ApiResponse<List<ReturnCardDto>>>(url);
        return (response?.Data ?? new(), response?.Pagination);
    }

    public async Task<List<ReturnCardDto>> GetOpenLabelsAsync()
    {
        var response = await _http.GetFromJsonAsync<ApiResponse<List<ReturnCardDto>>>("api/returns/labels");
        return response?.Data ?? new();
    }

    public async Task<List<AwaitingRefundDto>> GetAwaitingRefundAsync()
    {
        var response = await _http.GetFromJsonAsync<ApiResponse<List<AwaitingRefundDto>>>("api/returns/awaiting-refund");
        return response?.Data ?? new();
    }
}

public class ReturnCardDto
{
    public Guid ReturnId { get; set; }
    public Guid OrderId { get; set; }
    public string? ExternalOrderNumber { get; set; }
    public string? RetailerName { get; set; }
    public string? RMANumber { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ReturnReason { get; set; }
    public string? ReturnMethod { get; set; }
    public string? ReturnCarrier { get; set; }
    public string? ReturnTrackingNumber { get; set; }
    public string? ReturnTrackingUrl { get; set; }
    public bool HasPrintableLabel { get; set; }
    public string? QRCodeData { get; set; }
    public string? DropOffLocation { get; set; }
    public string? DropOffAddress { get; set; }
    public DateOnly? ReturnByDate { get; set; }
    public DateOnly? ReceivedByRetailerDate { get; set; }
    public string? RejectionReason { get; set; }
    public decimal? EstimatedRefundAmount { get; set; }
    public string? EstimatedRefundTimeline { get; set; }
    public List<ReturnLineItemDto> Items { get; set; } = new();
}

public class ReturnLineItemDto
{
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string? ReturnReason { get; set; }
}

public class AwaitingRefundDto
{
    public Guid ReturnId { get; set; }
    public Guid OrderId { get; set; }
    public string? ExternalOrderNumber { get; set; }
    public string? RetailerName { get; set; }
    public string? RMANumber { get; set; }
    public decimal? EstimatedRefundAmount { get; set; }
    public string? EstimatedRefundTimeline { get; set; }
    public DateOnly? ReceivedByRetailerDate { get; set; }
    public List<ReturnLineItemDto> Items { get; set; } = new();
}
