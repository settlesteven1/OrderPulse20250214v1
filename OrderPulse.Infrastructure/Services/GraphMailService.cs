using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace OrderPulse.Infrastructure.Services;

/// <summary>
/// Wraps Microsoft Graph API calls for reading tenant mailboxes.
/// Uses client credentials (app-only) with per-tenant consent.
/// Handles Graph API throttling with exponential backoff.
/// </summary>
public class GraphMailService
{
    private readonly GraphServiceClient _client;
    private readonly ILogger<GraphMailService> _logger;

    private const int MaxRetries = 3;
    private const int MaxMessagesPerPoll = 50;

    public GraphMailService(IConfiguration configuration, ILogger<GraphMailService> logger)
    {
        _logger = logger;

        var clientId = configuration["GraphApi:ClientId"]
            ?? throw new InvalidOperationException("GraphApi:ClientId is not configured");
        var clientSecret = configuration["GraphApi:ClientSecret"]
            ?? throw new InvalidOperationException("GraphApi:ClientSecret is not configured");
        var tenantId = configuration["GraphApi:TenantId"]
            ?? throw new InvalidOperationException("GraphApi:TenantId is not configured");

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        _client = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
    }

    /// <summary>
    /// Fetches new messages from a mailbox since the given timestamp.
    /// Returns messages ordered by receivedDateTime ascending.
    /// </summary>
    public async Task<IReadOnlyList<Message>> GetNewMessagesAsync(
        string mailboxAddress,
        DateTime? sinceDateUtc,
        CancellationToken ct = default)
    {
        var allMessages = new List<Message>();
        var sinceFilter = sinceDateUtc?.ToString("yyyy-MM-ddTHH:mm:ssZ") ?? "1970-01-01T00:00:00Z";

        try
        {
            var response = await ExecuteWithRetryAsync(async () =>
                await _client.Users[mailboxAddress]
                    .MailFolders["Inbox"]
                    .Messages
                    .GetAsync(config =>
                    {
                        config.QueryParameters.Filter =
                            $"receivedDateTime ge {sinceFilter}";
                        config.QueryParameters.Select = new[]
                        {
                            "id", "from", "subject", "receivedDateTime",
                            "body", "bodyPreview", "hasAttachments", "internetMessageId"
                        };
                        config.QueryParameters.Top = MaxMessagesPerPoll;
                        config.QueryParameters.Orderby = new[] { "receivedDateTime asc" };
                    }, ct), ct);

            if (response?.Value is not null)
            {
                allMessages.AddRange(response.Value);
            }

            _logger.LogInformation("Fetched {count} messages from {mailbox} since {since}",
                allMessages.Count, mailboxAddress, sinceFilter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch messages from {mailbox}", mailboxAddress);
            throw;
        }

        return allMessages;
    }

    /// <summary>
    /// Executes a Graph API call with exponential backoff retry on throttling (429).
    /// </summary>
    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
                when (ex.ResponseStatusCode == 429 && attempt < MaxRetries)
            {
                // Use exponential backoff: 2, 4, 8 seconds
                var retryAfterSeconds = Math.Pow(2, attempt + 1);

                _logger.LogWarning(
                    "Graph API throttled (429) on attempt {attempt}. Retrying in {delay}s",
                    attempt + 1, retryAfterSeconds);

                await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds), ct);
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
                when (ex.ResponseStatusCode >= 500 && attempt < MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                _logger.LogWarning(ex, "Graph API server error on attempt {attempt}. Retrying in {delay}s",
                    attempt + 1, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }

        // Final attempt — let exception propagate
        return await operation();
    }
}
