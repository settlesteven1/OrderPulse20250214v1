using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Enums;
using OrderPulse.Domain.Interfaces;

namespace OrderPulse.Infrastructure.AI;

/// <summary>
/// Implements IEmailClassifier using Azure OpenAI.
/// Pre-filter uses the classifier endpoint (GPT-4o-mini) for cost efficiency.
/// Full classification uses the parser endpoint (GPT-4o) for accuracy.
/// </summary>
public class EmailClassifierService : IEmailClassifier
{
    private readonly AzureOpenAIService _ai;
    private readonly ILogger<EmailClassifierService> _logger;
    private readonly Lazy<string> _preFilterPrompt;
    private readonly Lazy<string> _classifierPrompt;

    public EmailClassifierService(AzureOpenAIService ai, ILogger<EmailClassifierService> logger)
    {
        _ai = ai;
        _logger = logger;
        _preFilterPrompt = new Lazy<string>(() => AzureOpenAIService.LoadPrompt("PreFilterPrompt.md"));
        _classifierPrompt = new Lazy<string>(() => AzureOpenAIService.LoadPrompt("ClassifierPrompt.md"));
    }

    public async Task<bool> IsOrderRelatedAsync(
        string subject, string bodyPreview, string fromAddress, CancellationToken ct = default)
    {
        var userPrompt = $"Subject: {subject}\nFrom: {fromAddress}\nPreview: {bodyPreview}";

        try
        {
            var response = await _ai.ClassifierCompleteAsync(_preFilterPrompt.Value, userPrompt, jsonMode: true, ct);
            var result = _ai.DeserializeResponse<PreFilterResponse>(response);

            if (result is null)
            {
                _logger.LogWarning("Failed to parse pre-filter response, defaulting to true");
                return true; // Err on the side of processing
            }

            _logger.LogInformation("Pre-filter result: {isOrderRelated} for subject: {subject}",
                result.IsOrderRelated, subject.Length > 80 ? subject[..80] : subject);

            return result.IsOrderRelated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pre-filter failed for subject: {subject}", subject);
            return true; // Err on the side of processing
        }
    }

    public async Task<ClassificationResult> ClassifyAsync(
        string subject, string body, string fromAddress, CancellationToken ct = default)
    {
        var userPrompt = $"Subject: {subject}\nFrom: {fromAddress}\n\nEmail Body:\n{body}";

        try
        {
            var response = await _ai.ParserCompleteAsync(_classifierPrompt.Value, userPrompt, jsonMode: true, ct);
            var result = _ai.DeserializeResponse<ClassifierResponse>(response);

            if (result is null)
            {
                _logger.LogWarning("Failed to parse classifier response, returning Promotional with low confidence");
                return new ClassificationResult(EmailClassificationType.Promotional, 0.1m);
            }

            var classificationType = ParseClassificationType(result.Type);
            var confidence = Math.Clamp((decimal)result.Confidence, 0m, 1m);

            _logger.LogInformation("Classified email as {type} (confidence: {confidence}) — {subject}",
                classificationType, confidence, subject.Length > 80 ? subject[..80] : subject);

            return new ClassificationResult(classificationType, confidence, result.SecondaryType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Classification failed for subject: {subject}", subject);
            throw;
        }
    }

    private static EmailClassificationType ParseClassificationType(string type)
    {
        if (Enum.TryParse<EmailClassificationType>(type, ignoreCase: true, out var result))
            return result;

        // Handle common variations
        return type.ToLowerInvariant().Replace(" ", "").Replace("_", "") switch
        {
            "orderconfirmation" => EmailClassificationType.OrderConfirmation,
            "ordermodification" => EmailClassificationType.OrderModification,
            "ordercancellation" => EmailClassificationType.OrderCancellation,
            "paymentconfirmation" => EmailClassificationType.PaymentConfirmation,
            "shipmentconfirmation" => EmailClassificationType.ShipmentConfirmation,
            "shipmentupdate" => EmailClassificationType.ShipmentUpdate,
            "deliveryconfirmation" => EmailClassificationType.DeliveryConfirmation,
            "deliveryissue" => EmailClassificationType.DeliveryIssue,
            "returninitiation" => EmailClassificationType.ReturnInitiation,
            "returnlabel" => EmailClassificationType.ReturnLabel,
            "returnreceived" => EmailClassificationType.ReturnReceived,
            "returnrejection" => EmailClassificationType.ReturnRejection,
            "refundconfirmation" => EmailClassificationType.RefundConfirmation,
            "promotional" => EmailClassificationType.Promotional,
            _ => EmailClassificationType.Promotional
        };
    }

    private record PreFilterResponse(bool IsOrderRelated);

    private record ClassifierResponse(
        string Type,
        double Confidence,
        string? SecondaryType,
        string? Reasoning);
}
