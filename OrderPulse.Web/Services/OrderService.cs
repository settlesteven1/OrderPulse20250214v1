using System.Net.Http.Json;

namespace OrderPulse.Web.Services;

public class OrderService
{
    private readonly HttpClient _http;

    public OrderService(HttpClient http)
    {
        _http = http;
    }

    public async Task<(List<OrderListItem> Items, PaginationMeta? Pagination)> GetOrdersAsync(
        string? shortcut = null,
        string? status = null,
        string? search = null,
        int page = 1,
        int pageSize = 25,
        string sort = "OrderDate",
        bool desc = true)
    {
        var queryParams = new List<string> { $"page={page}", $"pageSize={pageSize}", $"sort={sort}", $"desc={desc}" };

        if (!string.IsNullOrEmpty(shortcut)) queryParams.Add($"shortcut={shortcut}");
        if (!string.IsNullOrEmpty(status)) queryParams.Add($"status={status}");
        if (!string.IsNullOrEmpty(search)) queryParams.Add($"search={Uri.EscapeDataString(search)}");

        var url = $"api/orders?{string.Join("&", queryParams)}";
        var response = await _http.GetFromJsonAsync<ApiResponse<List<OrderListItem>>>(url);

        return (response?.Data ?? new(), response?.Pagination);
    }

    public async Task<OrderDetailModel?> GetOrderDetailModelAsync(Guid orderId)
    {
        var response = await _http.GetFromJsonAsync<ApiResponse<OrderDetailModel>>($"api/orders/{orderId}");
        return response?.Data;
    }

    public async Task<List<TimelineEvent>> GetTimelineAsync(Guid orderId)
    {
        var response = await _http.GetFromJsonAsync<ApiResponse<List<TimelineEvent>>>(
            $"api/orders/{orderId}/timeline");
        return response?.Data ?? new();
    }
}
