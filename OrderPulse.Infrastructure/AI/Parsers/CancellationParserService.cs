using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Interfaces;

namespace OrderPulse.Infrastructure.AI.Parsers;

/// <summary>
/// Parses order cancellation emails (full and partial).
/// Uses the classifier endpoint (GPT-4o-mini) — cancellation emails are typically structured.
/// </summary>
public class CancellationParserService : IEmailParser<CancellationParserResult>
{
    private readonly AzureOpenAIService _ai;
    private readonly ILogger<CancellationParserService> _logger;
    private readonly Lazy<string> _systemPrompt;

    public CancellationParserService(AzureOpenAIService ai, ILogger<CancellationParserService> logger)
    {
        _ai = ai;
        _logger = logger;
        _systemPrompt = new Lazy<string>(() => AzureOpenAIService.LoadPrompt("CancellationParserPrompt.md"));
    }

    public async Task<ParseResult<CancellationParserResult>> ParseAsync(
        string subject, string body, string fromAddress, string? retailerContext, CancellationToken ct = default)
    {
        var userPrompt = $"Subject: {subject}\nFrom: {fromAddress}\n\nEmail Body:\n{body}";

        try
        {
            var response = await _ai.ClassifierCompleteAsync(_systemPrompt.Value, userPrompt, jsonMode: true, ct);
            var result = _ai.DeserializeResponse<CancellationParserResult>(response);

            if (result?.Cancellation is null)
            {
                _logger.LogWarning("Failed to parse cancellation from email: {subject}", subject);
                return new ParseResult<CancellationParserResult>(null, 0m, true, "Failed to parse AI response");
            }

            var confidence = Math.Clamp((decimal)result.Confidence, 0m, 1m);
            return new ParseResult<CancellationParserResult>(result, confidence, confidence < 0.7m);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cancellation parsing failed for: {subject}", subject);
            return new ParseResult<CancellationParserResult>(null, 0m, true, ex.Message);
        }
    }
}
