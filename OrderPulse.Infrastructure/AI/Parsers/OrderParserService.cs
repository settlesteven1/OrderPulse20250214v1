using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Interfaces;

namespace OrderPulse.Infrastructure.AI.Parsers;

/// <summary>
/// Parses order confirmation and order modification emails.
/// Uses the parser endpoint (GPT-4o) for accuracy on complex extractions.
/// </summary>
public class OrderParserService : IEmailParser<OrderParserResult>
{
    private readonly AzureOpenAIService _ai;
    private readonly ILogger<OrderParserService> _logger;
    private readonly Lazy<string> _systemPrompt;

    public OrderParserService(AzureOpenAIService ai, ILogger<OrderParserService> logger)
    {
        _ai = ai;
        _logger = logger;
        _systemPrompt = new Lazy<string>(() => AzureOpenAIService.LoadPrompt("OrderParserPrompt.md"));
    }

    public async Task<ParseResult<OrderParserResult>> ParseAsync(
        string subject, string body, string fromAddress, string? retailerContext, CancellationToken ct = default)
    {
        var userPrompt = FormatUserPrompt(subject, body, fromAddress, retailerContext);

        try
        {
            var response = await _ai.ParserCompleteAsync(_systemPrompt.Value, userPrompt, jsonMode: true, ct);
            var result = _ai.DeserializeResponse<OrderParserResult>(response);

            if (result?.Order is null)
            {
                _logger.LogWarning("Failed to parse order from email: {subject}", subject);
                return new ParseResult<OrderParserResult>(null, 0m, true, "Failed to parse AI response");
            }

            var confidence = Math.Clamp((decimal)result.Confidence, 0m, 1m);
            return new ParseResult<OrderParserResult>(result, confidence, confidence < 0.7m);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Order parsing failed for: {subject}", subject);
            return new ParseResult<OrderParserResult>(null, 0m, true, ex.Message);
        }
    }

    private static string FormatUserPrompt(string subject, string body, string fromAddress, string? retailerContext)
    {
        var prompt = $"Subject: {subject}\nFrom: {fromAddress}\n\nEmail Body:\n{body}";
        if (!string.IsNullOrEmpty(retailerContext))
            prompt += $"\n\nKnown retailer context: {retailerContext}";
        return prompt;
    }
}
