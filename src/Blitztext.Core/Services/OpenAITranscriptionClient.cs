using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blitztext.Core.Abstractions;

namespace Blitztext.Core.Services;

public sealed class OpenAITranscriptionClient(HttpClient httpClient, Func<CancellationToken, Task<string?>> apiKeyProvider) : ITranscriptionBackend
{
    // gpt-transcribe (July 2026) replaces gpt-4o-mini-transcribe: same speed, clearly lower error
    // rate, and literal biasing terms via `keywords` instead of a free-text prompt.
    public const string DefaultModel = "gpt-transcribe";
    private static readonly Uri TranscriptionsUri = new("https://api.openai.com/v1/audio/transcriptions");

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        var apiKey = await apiKeyProvider(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI API Key fehlt. Bitte in den Einstellungen hinterlegen.");
        }

        await using var audioStream = File.OpenRead(request.AudioPath);
        using var form = new MultipartFormDataContent();
        using var audioContent = new StreamContent(audioStream);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFor(request.AudioPath));
        form.Add(audioContent, "file", Path.GetFileName(request.AudioPath));
        form.Add(new StringContent(DefaultModel), "model");

        foreach (var keyword in OpenAITranscriptionOptions.Keywords(request.CustomTerms))
        {
            form.Add(new StringContent(keyword), "keywords[]");
        }

        foreach (var language in OpenAITranscriptionOptions.Languages(request.Language))
        {
            form.Add(new StringContent(language), "languages[]");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, TranscriptionsUri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = form;

        using var response = await httpClient.SendAsync(message, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI-Fehler: {ParseOpenAIError(content) ?? $"Status {(int)response.StatusCode}"}");
        }

        var text = ParseTranscript(content)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Transkription fehlgeschlagen.");
        }

        return text;
    }

    private static string ContentTypeFor(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".m4a" => "audio/m4a",
            ".mp3" => "audio/mpeg",
            ".mp4" => "audio/mp4",
            ".mpeg" => "audio/mpeg",
            ".mpga" => "audio/mpeg",
            ".webm" => "audio/webm",
            _ => "audio/wav"
        };
    }

    private static string? ParseTranscript(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TranscriptionResponse>(json)?.Text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ParseOpenAIError(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OpenAIErrorResponse>(json)?.Error?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record TranscriptionResponse([property: JsonPropertyName("text")] string? Text);

    private sealed record OpenAIErrorResponse([property: JsonPropertyName("error")] ApiError? Error);

    private sealed record ApiError([property: JsonPropertyName("message")] string? Message);
}
