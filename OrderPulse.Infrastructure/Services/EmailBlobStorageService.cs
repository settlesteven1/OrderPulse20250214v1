using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OrderPulse.Infrastructure.Services;

/// <summary>
/// Stores and retrieves raw email body HTML in Azure Blob Storage.
/// Organized by tenant ID and date for efficient management.
/// </summary>
public class EmailBlobStorageService
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<EmailBlobStorageService> _logger;

    private const string ContainerName = "email-bodies";

    public EmailBlobStorageService(IConfiguration configuration, ILogger<EmailBlobStorageService> logger)
    {
        _logger = logger;

        var connectionString = configuration["ConnectionStrings:BlobStorage"]
            ?? configuration["BlobStorageConnection"]
            ?? throw new InvalidOperationException("Blob storage connection string is not configured");

        var serviceClient = new BlobServiceClient(connectionString);
        _container = serviceClient.GetBlobContainerClient(ContainerName);
    }

    /// <summary>
    /// Stores an email body HTML in blob storage.
    /// Returns the blob URL for later retrieval.
    /// Path format: {tenantId}/{yyyy-MM}/{graphMessageId}.html
    /// </summary>
    public async Task<string> StoreEmailBodyAsync(
        Guid tenantId, string graphMessageId, string bodyHtml, CancellationToken ct = default)
    {
        var blobName = $"{tenantId}/{DateTime.UtcNow:yyyy-MM}/{graphMessageId}.html";

        try
        {
            await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);

            var blob = _container.GetBlobClient(blobName);
            var content = Encoding.UTF8.GetBytes(bodyHtml);

            await blob.UploadAsync(
                new BinaryData(content),
                new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = "text/html; charset=utf-8" }
                },
                ct);

            _logger.LogDebug("Stored email body blob: {blobName}", blobName);
            return blob.Uri.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store email body blob: {blobName}", blobName);
            throw;
        }
    }

    /// <summary>
    /// Retrieves an email body HTML from blob storage by its URL.
    /// </summary>
    public async Task<string?> GetEmailBodyAsync(string blobUrl, CancellationToken ct = default)
    {
        try
        {
            var uri = new Uri(blobUrl);
            // Extract blob name from the URL path (skip the container name segment)
            var pathSegments = uri.AbsolutePath.TrimStart('/').Split('/', 2);
            if (pathSegments.Length < 2)
            {
                _logger.LogWarning("Blob URL has insufficient path segments: {url}", blobUrl);
                return null;
            }

            var blobName = Uri.UnescapeDataString(pathSegments[1]); // unescape %xx sequences
            _logger.LogInformation("Fetching blob: container={container}, blobName={blobName}, url={url}",
                _container.Name, blobName, blobUrl);

            var blob = _container.GetBlobClient(blobName);
            var exists = await blob.ExistsAsync(ct);
            if (!exists.Value)
            {
                _logger.LogWarning("Blob does not exist: {blobName}", blobName);
                return null;
            }

            var response = await blob.DownloadContentAsync(ct);
            var content = response.Value.Content.ToString();
            _logger.LogInformation("Blob retrieved: {len} chars", content?.Length ?? 0);
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve email body from: {url}", blobUrl);
            return null;
        }
    }
}
