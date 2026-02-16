using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Interfaces;

namespace OrderPulse.Infrastructure.AI.Parsers;

/// <summary>
/// Parses refund confirmation emails.
/// Uses the classifier endpoint (GPT-4o-mini) — refund emails are typically straightforward.
/// </summary>
public class RefundParserService : IEmailParser<RefundParserResult>
{
    private readonly AzureOpenAIService _ai;
    private readonly ILogger<RefundParserService> _logger;
    private readonly Lazy<string> _systemPrompt;

    public RefundParserService(AzureOpenAIService ai, ILogger<RefundParserService> logger)
    {
        _ai = ai;
        _logger = logger;
        _systemPrompt = new Lazy<string>(() => AzureOpenAIService.LoadPrompt("RefundParserPrompt.md"));
    }

    public async Task<ParseResult<RefundParserResult>> ParseAsync(
        string subject, string body, string fromAddress, string? retailerContext, CancellationToken ct = default)
    {
        var userPrompt = $"Subject: {subject}\nFrom: {fromAddress}\n\nEmail Body:\n{body}";

        try
        {
            var response = await _ai.ClassifierCompleteAsync(_systemPrompt.Value, userPrompt, jsonMode: true, ct);
            var result = _ai.DeserializeResponse<RefundParserResult>(response);

            if (result?.Refund is null)
            {
                _logger.LogWarning("Failed to parse refund from email: {subject}", subject);
                return new ParseResult<RefundParserResult>(null, 0m, true, "Failed to parse AI response");
            }

            var confidence = Math.Clamp((decimal)result.Confidence, 0m, 1m);
            return new ParseResult<RefundParserResult>(result, confidence, confidence < 0.7m);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refund parsing failed for: {subject}", subject);
            return new ParseResult<RefundParserResult>(null, 0m, true, ex.Message);
        }
    }
}
