using System.Net.Http.Json;

namespace OrderPulse.Web.Services;

public class InventoryService
{
    private readonly HttpClient _http;

    public InventoryService(HttpClient http)
    {
        _http = http;
    }

    public async Task<(List<InventoryItemModel> Items, PaginationMeta? Pagination)> GetInventoryAsync(
        string? category = null,
        string? search = null,
        int page = 1,
        int pageSize = 25)
    {
        var queryParams = new List<string> { $"page={page}", $"pageSize={pageSize}" };

        if (!string.IsNullOrEmpty(category)) queryParams.Add($"category={category}");
        if (!string.IsNullOrEmpty(search)) queryParams.Add($"search={Uri.EscapeDataString(search)}");

        var url = $"api/inventory?{string.Join("&", queryParams)}";
        var response = await _http.GetFromJsonAsync<ApiResponse<List<InventoryItemModel>>>(url);

        return (response?.Data ?? new(), response?.Meta);
    }

    public async Task<InventoryItemDetailModel?> GetInventoryItemAsync(Guid id)
    {
        var response = await _http.GetFromJsonAsync<ApiResponse<InventoryItemDetailModel>>(
            $"api/inventory/{id}");
        return response?.Data;
    }

    public async Task<bool> AdjustInventoryAsync(Guid id, int quantityDelta, string reason, string? notes)
    {
        var request = new { QuantityDelta = quantityDelta, Reason = reason, Notes = notes };
        var response = await _http.PostAsJsonAsync($"api/inventory/{id}/adjust", request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateCategoryAsync(Guid id, string category)
    {
        var request = new { Category = category };
        var response = await _http.PutAsJsonAsync($"api/inventory/{id}/category", request);
        return response.IsSuccessStatusCode;
    }

    public async Task<List<InventoryAdjustmentModel>> GetAdjustmentsAsync(Guid id)
    {
        var response = await _http.GetFromJsonAsync<ApiResponse<List<InventoryAdjustmentModel>>>(
            $"api/inventory/{id}/adjustments");
        return response?.Data ?? new();
    }
}
