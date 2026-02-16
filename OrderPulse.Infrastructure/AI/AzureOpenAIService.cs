using System.ClientModel;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace OrderPulse.Infrastructure.AI;

/// <summary>
/// Base wrapper around Azure OpenAI providing two client instances:
/// one for the classifier endpoint (GPT-4o-mini) and one for the parser endpoint (GPT-4o).
/// Handles prompt loading from embedded markdown files, JSON response parsing, and retry logic.
/// </summary>
public class AzureOpenAIService
{
    private readonly ChatClient _classifierClient;
    private readonly ChatClient _parserClient;
    private readonly ILogger<AzureOpenAIService> _logger;

    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

    public AzureOpenAIService(IConfiguration configuration, ILogger<AzureOpenAIService> logger)
    {
        _logger = logger;

        var apiKey = configuration["AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("AzureOpenAI:ApiKey is not configured");
        var classifierEndpoint = configuration["AzureOpenAI:ClassifierEndpoint"]
            ?? throw new InvalidOperationException("AzureOpenAI:ClassifierEndpoint is not configured");
        var parserEndpoint = configuration["AzureOpenAI:ParserEndpoint"]
            ?? throw new InvalidOperationException("AzureOpenAI:ParserEndpoint is not configured");
        var classifierDeployment = configuration["AzureOpenAI:ClassifierDeployment"] ?? "orderpulse-classifier";
        var parserDeployment = configuration["AzureOpenAI:ParserDeployment"] ?? "orderpulse-parser";

        var classifierAzureClient = new AzureOpenAIClient(new Uri(classifierEndpoint), new AzureKeyCredential(apiKey));
        var parserAzureClient = new AzureOpenAIClient(new Uri(parserEndpoint), new AzureKeyCredential(apiKey));

        _classifierClient = classifierAzureClient.GetChatClient(classifierDeployment);
        _parserClient = parserAzureClient.GetChatClient(parserDeployment);
    }

    /// <summary>
    /// Sends a chat completion request to the classifier endpoint (GPT-4o-mini).
    /// Used for pre-filtering and cost-efficient tasks.
    /// </summary>
    public async Task<string> ClassifierCompleteAsync(
        string systemPrompt,
        string userPrompt,
        bool jsonMode = true,
        CancellationToken ct = default)
    {
        return await CompleteWithRetryAsync(_classifierClient, systemPrompt, userPrompt, jsonMode, ct);
    }

    /// <summary>
    /// Sends a chat completion request to the parser endpoint (GPT-4o).
    /// Used for complex parsing tasks requiring higher accuracy.
    /// </summary>
    public async Task<string> ParserCompleteAsync(
        string systemPrompt,
        string userPrompt,
        bool jsonMode = true,
        CancellationToken ct = default)
    {
        return await CompleteWithRetryAsync(_parserClient, systemPrompt, userPrompt, jsonMode, ct);
    }

    /// <summary>
    /// Deserializes a JSON response into the specified type.
    /// Returns null if deserialization fails.
    /// </summary>
    public T? DeserializeResponse<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize AI response: {json}", json.Length > 500 ? json[..500] : json);
            return null;
        }
    }

    /// <summary>
    /// Loads a prompt template from the AI/Prompts directory.
    /// Extracts just the system prompt section from the markdown.
    /// </summary>
    public static string LoadPrompt(string promptFileName)
    {
        var basePath = AppContext.BaseDirectory;
        var promptPath = Path.Combine(basePath, "AI", "Prompts", promptFileName);

        if (!File.Exists(promptPath))
        {
            // Try relative to the assembly location
            var assemblyDir = Path.GetDirectoryName(typeof(AzureOpenAIService).Assembly.Location) ?? "";
            promptPath = Path.Combine(assemblyDir, "AI", "Prompts", promptFileName);
        }

        if (!File.Exists(promptPath))
            throw new FileNotFoundException($"Prompt file not found: {promptFileName}", promptPath);

        var content = File.ReadAllText(promptPath);
        return ExtractSystemPrompt(content);
    }

    private async Task<string> CompleteWithRetryAsync(
        ChatClient client,
        string systemPrompt,
        string userPrompt,
        bool jsonMode,
        CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        var options = new ChatCompletionOptions();
        if (jsonMode)
        {
            options.ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat();
        }

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var response = await client.CompleteChatAsync(messages, options, ct);
                var content = response.Value.Content[0].Text;

                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogWarning("Empty response from AI on attempt {attempt}", attempt + 1);
                    continue;
                }

                return content;
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                _logger.LogWarning("Rate limited on attempt {attempt}, retrying in {delay}s",
                    attempt + 1, RetryDelays[attempt].TotalSeconds);
                await Task.Delay(RetryDelays[attempt], ct);
            }
            catch (ClientResultException ex) when (ex.Status >= 500)
            {
                _logger.LogWarning(ex, "Server error on attempt {attempt}, retrying", attempt + 1);
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }

        throw new InvalidOperationException("AI completion failed after all retry attempts");
    }

    /// <summary>
    /// Extracts the system prompt section from a markdown prompt file.
    /// Looks for content between "## System Prompt" and the next "## " header.
    /// </summary>
    private static string ExtractSystemPrompt(string markdownContent)
    {
        var lines = markdownContent.Split('\n');
        var capturing = false;
        var promptLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("## System Prompt"))
            {
                capturing = true;
                continue;
            }

            if (capturing && line.TrimStart().StartsWith("## "))
            {
                break;
            }

            if (capturing)
            {
                promptLines.Add(line);
            }
        }

        return string.Join('\n', promptLines).Trim();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}
