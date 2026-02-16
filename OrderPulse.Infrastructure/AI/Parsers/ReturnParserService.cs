using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Interfaces;

namespace OrderPulse.Infrastructure.AI.Parsers;

/// <summary>
/// Parses return-related emails (initiation, label, received, rejection).
/// Uses the parser endpoint (GPT-4o) — return emails are complex with multiple subtypes.
/// </summary>
public class ReturnParserService : IEmailParser<ReturnParserResult>
{
    private readonly AzureOpenAIService _ai;
    private readonly ILogger<ReturnParserService> _logger;
    private readonly Lazy<string> _systemPrompt;

    public ReturnParserService(AzureOpenAIService ai, ILogger<ReturnParserService> logger)
    {
        _ai = ai;
        _logger = logger;
        _systemPrompt = new Lazy<string>(() => AzureOpenAIService.LoadPrompt("ReturnParserPrompt.md"));
    }

    public async Task<ParseResult<ReturnParserResult>> ParseAsync(
        string subject, string body, string fromAddress, string? retailerContext, CancellationToken ct = default)
    {
        var userPrompt = $"Subject: {subject}\nFrom: {fromAddress}\n\nEmail Body:\n{body}";
        if (!string.IsNullOrEmpty(retailerContext))
            userPrompt += $"\n\nKnown retailer context: {retailerContext}";

        try
        {
            var response = await _ai.ParserCompleteAsync(_systemPrompt.Value, userPrompt, jsonMode: true, ct);
            var result = _ai.DeserializeResponse<ReturnParserResult>(response);

            if (result?.Return is null)
            {
                _logger.LogWarning("Failed to parse return from email: {subject}", subject);
                return new ParseResult<ReturnParserResult>(null, 0m, true, "Failed to parse AI response");
            }

            var confidence = Math.Clamp((decimal)result.Confidence, 0m, 1m);
            return new ParseResult<ReturnParserResult>(result, confidence, confidence < 0.7m);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Return parsing failed for: {subject}", subject);
            return new ParseResult<ReturnParserResult>(null, 0m, true, ex.Message);
        }
    }
}
