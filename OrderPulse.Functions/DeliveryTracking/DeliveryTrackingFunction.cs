using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Entities;
using OrderPulse.Domain.Enums;
using OrderPulse.Infrastructure.AI;
using OrderPulse.Infrastructure.Data;
using OrderPulse.Infrastructure.Services;

namespace OrderPulse.Functions.DeliveryTracking;

/// <summary>
/// Timer-triggered function that checks shipped packages for delivery confirmation.
/// Runs once daily at 8:00 AM UTC. For each undelivered shipment with a tracking number,
/// fetches the carrier tracking page, uses GPT-4o-mini to extract delivery status,
/// and creates Delivery records when packages are confirmed delivered.
/// </summary>
public class DeliveryTrackingFunction
{
    private readonly ILogger<DeliveryTrackingFunction> _logger;
    private readonly OrderPulseDbContext _db;
    private readonly AzureOpenAIService _ai;
    private readonly IHttpClientFactory _httpFactory;
    private readonly OrderStateMachine _stateMachine;
    private readonly ProcessingLogger _log;

    private static readonly Lazy<string> TrackingPrompt = new(() =>
        AzureOpenAIService.LoadPrompt("DeliveryTrackingPrompt.md"));

    /// <summary>Maximum shipment age to track (don't track ancient shipments forever).</summary>
    private const int MaxShipmentAgeDays = 30;

    /// <summary>Maximum shipments to check per run (to control AI costs).</summary>
    private const int MaxShipmentsPerRun = 50;

    public DeliveryTrackingFunction(
        ILogger<DeliveryTrackingFunction> logger,
        OrderPulseDbContext db,
        AzureOpenAIService ai,
        IHttpClientFactory httpFactory,
        OrderStateMachine stateMachine,
        ProcessingLogger log)
    {
        _logger = logger;
        _db = db;
        _ai = ai;
        _httpFactory = httpFactory;
        _stateMachine = stateMachine;
        _log = log;
    }

    [Function("DeliveryTrackingFunction")]
    public async Task Run(
        [TimerTrigger("0 0 8 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        _logger.LogInformation("Delivery tracking check started at {time}", DateTime.UtcNow);

        var cutoffDate = DateTime.UtcNow.AddDays(-MaxShipmentAgeDays);

        // Find shipped/in-transit shipments with tracking numbers but no delivery
        var shipments = await _db.Shipments
            .IgnoreQueryFilters()
            .Include(s => s.Order)
            .Where(s =>
                (s.Status == ShipmentStatus.Shipped ||
                 s.Status == ShipmentStatus.InTransit ||
                 s.Status == ShipmentStatus.OutForDelivery) &&
                s.TrackingNumber != null &&
                s.Delivery == null &&
                s.CreatedAt >= cutoffDate)
            .OrderBy(s => s.CreatedAt) // oldest first
            .Take(MaxShipmentsPerRun)
            .ToListAsync(ct);

        _logger.LogInformation("Found {count} shipments to check for delivery", shipments.Count);

        var deliveredCount = 0;
        var updatedCount = 0;
        var errorCount = 0;

        foreach (var shipment in shipments)
        {
            try
            {
                var result = await CheckShipmentDeliveryAsync(shipment, ct);

                if (result == TrackingResult.Delivered)
                    deliveredCount++;
                else if (result == TrackingResult.StatusUpdated)
                    updatedCount++;
            }
            catch (Exception ex)
            {
                errorCount++;
                _logger.LogWarning(ex,
                    "Failed to check delivery for shipment {id} (tracking: {tracking})",
                    shipment.ShipmentId, shipment.TrackingNumber);
            }

            // Brief delay between requests to avoid carrier rate limiting
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        _logger.LogInformation(
            "Delivery tracking complete: {total} checked, {delivered} delivered, {updated} updated, {errors} errors",
            shipments.Count, deliveredCount, updatedCount, errorCount);
    }

    /// <summary>
    /// Checks a single shipment's tracking page for delivery status.
    /// </summary>
    private async Task<TrackingResult> CheckShipmentDeliveryAsync(
        Shipment shipment, CancellationToken ct)
    {
        var carrier = CarrierDetector.Detect(shipment.TrackingNumber!);
        if (carrier is null)
        {
            _logger.LogDebug("Unknown carrier for tracking {tracking}, skipping",
                shipment.TrackingNumber);
            return TrackingResult.Skipped;
        }

        // Fetch the carrier tracking page
        var pageText = await FetchTrackingPageAsync(carrier, ct);
        if (string.IsNullOrWhiteSpace(pageText) || pageText.Length < 100)
        {
            _logger.LogDebug("Tracking page returned insufficient content for {tracking}",
                shipment.TrackingNumber);
            return TrackingResult.Skipped;
        }

        // Ask GPT-4o-mini to extract delivery status
        var userPrompt = $"Carrier: {carrier.CarrierName}\nTracking Number: {carrier.TrackingNumber}\n\nPage Content:\n{pageText}";
        var aiResponse = await _ai.ClassifierCompleteAsync(
            TrackingPrompt.Value, userPrompt, jsonMode: true, ct);

        var status = _ai.DeserializeResponse<TrackingStatusResponse>(aiResponse);
        if (status is null || status.Status == "Unknown")
        {
            _logger.LogDebug("Could not determine status for {tracking}", shipment.TrackingNumber);
            return TrackingResult.Skipped;
        }

        // Set tenant context for RLS
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"EXEC sp_set_session_context @key=N'TenantId', @value={shipment.TenantId.ToString()}", ct);

        if (status.Status == "Delivered")
        {
            return await RecordDeliveryAsync(shipment, status, carrier, ct);
        }

        // Update shipment status if it changed (e.g., Shipped → InTransit, InTransit → OutForDelivery)
        var newStatus = status.Status switch
        {
            "InTransit" => ShipmentStatus.InTransit,
            "OutForDelivery" => ShipmentStatus.OutForDelivery,
            _ => (ShipmentStatus?)null
        };

        if (newStatus.HasValue && newStatus.Value != shipment.Status)
        {
            shipment.Status = newStatus.Value;
            shipment.LastStatusUpdate = status.LastUpdate;
            shipment.LastStatusDate = DateTime.UtcNow;
            shipment.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Shipment {id} status updated to {status} (tracking: {tracking})",
                shipment.ShipmentId, newStatus.Value, shipment.TrackingNumber);

            return TrackingResult.StatusUpdated;
        }

        return TrackingResult.NoChange;
    }

    /// <summary>
    /// Records a confirmed delivery: creates Delivery record, updates Shipment status,
    /// recalculates order status, and creates a timeline event.
    /// </summary>
    private async Task<TrackingResult> RecordDeliveryAsync(
        Shipment shipment, TrackingStatusResponse status, CarrierInfo carrier, CancellationToken ct)
    {
        // Parse delivery date
        DateTime? deliveryDate = null;
        if (!string.IsNullOrEmpty(status.DeliveryDate))
        {
            if (DateTime.TryParse(status.DeliveryDate, out var parsed))
                deliveryDate = parsed;
        }
        deliveryDate ??= DateTime.UtcNow;

        // Create Delivery record
        var delivery = new Delivery
        {
            DeliveryId = Guid.NewGuid(),
            TenantId = shipment.TenantId,
            ShipmentId = shipment.ShipmentId,
            DeliveryDate = deliveryDate,
            DeliveryLocation = status.DeliveryLocation,
            Status = DeliveryStatus.Delivered,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Deliveries.Add(delivery);

        // Update Shipment status
        shipment.Status = ShipmentStatus.Delivered;
        shipment.LastStatusUpdate = status.LastUpdate ?? "Delivered (confirmed via tracking)";
        shipment.LastStatusDate = deliveryDate;
        shipment.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // Recalculate order status
        await _stateMachine.RecalculateStatusAsync(shipment.OrderId, ct);

        // Create timeline event
        _db.OrderEvents.Add(new OrderEvent
        {
            EventId = Guid.NewGuid(),
            TenantId = shipment.TenantId,
            OrderId = shipment.OrderId,
            EventType = "DeliveryConfirmed",
            EventDate = deliveryDate.Value,
            Summary = $"Package delivered ({carrier.CarrierName})",
            Details = $"Tracking: {shipment.TrackingNumber}" +
                      (status.DeliveryLocation is not null ? $" | Location: {status.DeliveryLocation}" : "") +
                      (status.SignedBy is not null ? $" | Signed by: {status.SignedBy}" : "") +
                      " | Source: Automated tracking check"
        });
        await _db.SaveChangesAsync(ct);

        await _log.Success(Guid.Empty, "DeliveryTracking",
            $"Delivery confirmed for shipment {shipment.ShipmentId} " +
            $"(order: {shipment.Order?.ExternalOrderNumber}, " +
            $"tracking: {shipment.TrackingNumber}, carrier: {carrier.CarrierName})");

        _logger.LogInformation(
            "Delivery confirmed for shipment {id} (tracking: {tracking}, carrier: {carrier})",
            shipment.ShipmentId, shipment.TrackingNumber, carrier.CarrierName);

        return TrackingResult.Delivered;
    }

    /// <summary>
    /// Fetches a carrier tracking page and returns the text content.
    /// Uses a browser-like User-Agent to avoid being blocked.
    /// Truncates to 15K chars to keep AI costs down.
    /// </summary>
    private async Task<string?> FetchTrackingPageAsync(CarrierInfo carrier, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("TrackingClient");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("text/html"));
            client.Timeout = TimeSpan.FromSeconds(15);

            var response = await client.GetAsync(carrier.TrackingUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Tracking page returned {status} for {url}",
                    response.StatusCode, carrier.TrackingUrl);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(ct);

            // Convert HTML to plain text for AI processing
            var text = ForwardedEmailHelper.ExtractOriginalBody(html);

            // Truncate to keep AI costs reasonable
            return text.Length > 15_000 ? text[..15_000] : text;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch tracking page for {tracking}", carrier.TrackingNumber);
            return null;
        }
    }

    private enum TrackingResult
    {
        Skipped,
        NoChange,
        StatusUpdated,
        Delivered
    }

    private record TrackingStatusResponse(
        string Status,
        string? DeliveryDate,
        string? DeliveryLocation,
        string? SignedBy,
        string? CurrentLocation,
        string? LastUpdate);
}
