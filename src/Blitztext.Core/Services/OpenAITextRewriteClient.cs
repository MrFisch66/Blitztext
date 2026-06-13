using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blitztext.Core.Abstractions;

namespace Blitztext.Core.Services;

public sealed class OpenAITextRewriteClient(HttpClient httpClient, Func<CancellationToken, Task<string?>> apiKeyProvider) : ITextRewriteClient
{
    private static readonly Uri ResponsesUri = new("https://api.openai.com/v1/responses");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string> RewriteAsync(TextRewriteRequest request, CancellationToken cancellationToken = default)
    {
        var apiKey = await apiKeyProvider(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI API Key fehlt. Bitte in den Einstellungen hinterlegen.");
        }

        var payload = new ResponsesRequest(
            request.Model,
            request.Instructions,
            request.Text,
            request.Temperature,
            Store: false);

        using var message = new HttpRequestMessage(HttpMethod.Post, ResponsesUri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(message, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Fehler von OpenAI: {ParseOpenAIError(content) ?? $"Status {(int)response.StatusCode}"}");
        }

        var text = ExtractOutputText(content);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Keine Antwort erhalten. Bitte nochmal versuchen.");
        }

        return text.Trim();
    }

    public static string? ExtractOutputText(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("output_text", out var outputText) &&
            outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString();
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var outputItem in output.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    parts.Add(text.GetString() ?? string.Empty);
                }
            }
        }

        return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
    }

    private static string? ParseOpenAIError(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OpenAIErrorResponse>(json, JsonOptions)?.Error?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ResponsesRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("instructions")] string Instructions,
        [property: JsonPropertyName("input")] string Input,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("store")] bool Store);

    private sealed record OpenAIErrorResponse([property: JsonPropertyName("error")] ApiError? Error);

    private sealed record ApiError([property: JsonPropertyName("message")] string? Message);
}
