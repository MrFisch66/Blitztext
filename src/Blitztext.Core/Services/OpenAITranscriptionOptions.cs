namespace Blitztext.Core.Services;

/// <summary>
/// Request options shared by the OpenAI file and live transcription clients.
/// </summary>
public static class OpenAITranscriptionOptions
{
    private static readonly char[] ForbiddenKeywordChars = ['<', '>', '\r', '\n'];

    /// <summary>
    /// Custom terms as `keywords`. The API rejects the whole request if a single keyword spans
    /// lines or contains angle brackets, so those characters are stripped up front.
    /// </summary>
    public static IReadOnlyList<string> Keywords(IReadOnlyList<string> customTerms) =>
        customTerms
            .Select(term => string.Join(' ', term.Split(ForbiddenKeywordChars, StringSplitOptions.RemoveEmptyEntries)).Trim())
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// The configured language as `languages`. "auto" or an empty value lets the model detect it.
    /// </summary>
    public static IReadOnlyList<string> Languages(string? language)
    {
        var trimmed = language?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed) || trimmed == "auto" ? [] : [trimmed];
    }
}
