using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blitztext.Core.Abstractions;

namespace Blitztext.Core.Services;

public sealed class OpenAITranscriptionClient(HttpClient httpClient, Func<CancellationToken, Task<string?>> apiKeyProvider) : ITranscriptionBackend
{
    public const string DefaultModel = "gpt-4o-mini-transcribe";
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
        form.Add(new StringContent("text"), "response_format");

        if (request.CustomTerms.Count > 0)
        {
            form.Add(new StringContent(TranscriptionQualityService.BuildCustomTermsPrompt(request.CustomTerms)), "prompt");
        }

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            form.Add(new StringContent(request.Language.Trim()), "language");
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

        var text = content.Trim();
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

    private sealed record OpenAIErrorResponse([property: JsonPropertyName("error")] ApiError? Error);

    private sealed record ApiError([property: JsonPropertyName("message")] string? Message);
}
