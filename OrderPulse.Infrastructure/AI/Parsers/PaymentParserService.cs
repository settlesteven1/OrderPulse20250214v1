using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Interfaces;

namespace OrderPulse.Infrastructure.AI.Parsers;

/// <summary>
/// Parses payment confirmation emails.
/// Uses the classifier endpoint (GPT-4o-mini) — payment emails are typically simple.
/// </summary>
public class PaymentParserService : IEmailParser<PaymentParserResult>
{
    private readonly AzureOpenAIService _ai;
    private readonly ILogger<PaymentParserService> _logger;
    private readonly Lazy<string> _systemPrompt;

    public PaymentParserService(AzureOpenAIService ai, ILogger<PaymentParserService> logger)
    {
        _ai = ai;
        _logger = logger;
        _systemPrompt = new Lazy<string>(() => AzureOpenAIService.LoadPrompt("PaymentParserPrompt.md"));
    }

    public async Task<ParseResult<PaymentParserResult>> ParseAsync(
        string subject, string body, string fromAddress, string? retailerContext, CancellationToken ct = default)
    {
        var userPrompt = $"Subject: {subject}\nFrom: {fromAddress}\n\nEmail Body:\n{body}";

        try
        {
            var response = await _ai.ClassifierCompleteAsync(_systemPrompt.Value, userPrompt, jsonMode: true, ct);
            var result = _ai.DeserializeResponse<PaymentParserResult>(response);

            if (result?.Payment is null)
            {
                _logger.LogWarning("Failed to parse payment from email: {subject}", subject);
                return new ParseResult<PaymentParserResult>(null, 0m, true, "Failed to parse AI response");
            }

            var confidence = Math.Clamp((decimal)result.Confidence, 0m, 1m);
            return new ParseResult<PaymentParserResult>(result, confidence, confidence < 0.7m);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payment parsing failed for: {subject}", subject);
            return new ParseResult<PaymentParserResult>(null, 0m, true, ex.Message);
        }
    }
}
