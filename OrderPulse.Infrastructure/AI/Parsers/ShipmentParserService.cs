using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Interfaces;

namespace OrderPulse.Infrastructure.AI.Parsers;

/// <summary>
/// Parses shipment confirmation and shipment update emails.
/// Uses the parser endpoint (GPT-4o) for accuracy with carrier/tracking extraction.
/// </summary>
public class ShipmentParserService : IEmailParser<ShipmentParserResult>
{
    private readonly AzureOpenAIService _ai;
    private readonly ILogger<ShipmentParserService> _logger;
    private readonly Lazy<string> _systemPrompt;

    public ShipmentParserService(AzureOpenAIService ai, ILogger<ShipmentParserService> logger)
    {
        _ai = ai;
        _logger = logger;
        _systemPrompt = new Lazy<string>(() => AzureOpenAIService.LoadPrompt("ShipmentParserPrompt.md"));
    }

    public async Task<ParseResult<ShipmentParserResult>> ParseAsync(
        string subject, string body, string fromAddress, string? retailerContext, CancellationToken ct = default)
    {
        var userPrompt = $"Subject: {subject}\nFrom: {fromAddress}\n\nEmail Body:\n{body}";
        if (!string.IsNullOrEmpty(retailerContext))
            userPrompt += $"\n\nKnown retailer context: {retailerContext}";

        try
        {
            var response = await _ai.ParserCompleteAsync(_systemPrompt.Value, userPrompt, jsonMode: true, ct);
            var result = _ai.DeserializeResponse<ShipmentParserResult>(response);

            if (result?.Shipments is null || result.Shipments.Count == 0)
            {
                _logger.LogWarning("Failed to parse shipment from email: {subject}", subject);
                return new ParseResult<ShipmentParserResult>(null, 0m, true, "Failed to parse AI response");
            }

            var confidence = Math.Clamp((decimal)result.Confidence, 0m, 1m);
            return new ParseResult<ShipmentParserResult>(result, confidence, confidence < 0.7m);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shipment parsing failed for: {subject}", subject);
            return new ParseResult<ShipmentParserResult>(null, 0m, true, ex.Message);
        }
    }
}
