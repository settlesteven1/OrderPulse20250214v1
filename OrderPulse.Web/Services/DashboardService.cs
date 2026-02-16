using System.Net.Http.Json;

namespace OrderPulse.Web.Services;

public class DashboardService
{
    private readonly HttpClient _http;

    public DashboardService(HttpClient http)
    {
        _http = http;
    }

    public async Task<DashboardSummary?> GetSummaryAsync()
    {
        var response = await _http.GetFromJsonAsync<ApiResponse<DashboardSummary>>("api/dashboard/summary");
        return response?.Data;
    }

    public async Task<List<TimelineEvent>> GetRecentActivityAsync(int count = 20)
    {
        // Uses the orders timeline endpoint with a special query for recent events across all orders
        var response = await _http.GetFromJsonAsync<ApiResponse<List<TimelineEvent>>>(
            $"api/dashboard/activity?count={count}");
        return response?.Data ?? new();
    }
}
