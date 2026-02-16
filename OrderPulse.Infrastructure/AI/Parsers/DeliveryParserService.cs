using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Interfaces;

namespace OrderPulse.Infrastructure.AI.Parsers;

/// <summary>
/// Parses delivery confirmation and delivery issue emails.
/// Uses the classifier endpoint (GPT-4o-mini) — delivery emails are typically straightforward.
/// </summary>
public class DeliveryParserService : IEmailParser<DeliveryParserResult>
{
    private readonly AzureOpenAIService _ai;
    private readonly ILogger<DeliveryParserService> _logger;
    private readonly Lazy<string> _systemPrompt;

    public DeliveryParserService(AzureOpenAIService ai, ILogger<DeliveryParserService> logger)
    {
        _ai = ai;
        _logger = logger;
        _systemPrompt = new Lazy<string>(() => AzureOpenAIService.LoadPrompt("DeliveryParserPrompt.md"));
    }

    public async Task<ParseResult<DeliveryParserResult>> ParseAsync(
        string subject, string body, string fromAddress, string? retailerContext, CancellationToken ct = default)
    {
        var userPrompt = $"Subject: {subject}\nFrom: {fromAddress}\n\nEmail Body:\n{body}";

        try
        {
            var response = await _ai.ClassifierCompleteAsync(_systemPrompt.Value, userPrompt, jsonMode: true, ct);
            var result = _ai.DeserializeResponse<DeliveryParserResult>(response);

            if (result?.Delivery is null)
            {
                _logger.LogWarning("Failed to parse delivery from email: {subject}", subject);
                return new ParseResult<DeliveryParserResult>(null, 0m, true, "Failed to parse AI response");
            }

            var confidence = Math.Clamp((decimal)result.Confidence, 0m, 1m);
            return new ParseResult<DeliveryParserResult>(result, confidence, confidence < 0.7m);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delivery parsing failed for: {subject}", subject);
            return new ParseResult<DeliveryParserResult>(null, 0m, true, ex.Message);
        }
    }
}
