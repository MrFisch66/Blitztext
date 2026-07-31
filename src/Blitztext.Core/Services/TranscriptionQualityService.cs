namespace Blitztext.Core.Services;

public static class TranscriptionQualityService
{
    public static readonly TimeSpan MinimumRecordingDuration = TimeSpan.FromSeconds(0.3);

    // A quick, accidental tap of the hotkey captures near-silence. Transcribers then hallucinate
    // filler phrases ("Vielen Dank.", "Untertitel …") out of it, which used to be pasted as if the
    // user had spoken. Treat a short recording whose loudest sample never crosses this floor as
    // "no speech" and drop it before transcription. The stricter threshold is limited to short
    // recordings so a deliberately quiet but real utterance of normal length is still transcribed.
    public static readonly float SilencePeakThreshold = 0.02f;
    public static readonly TimeSpan SilenceCheckMaxDuration = TimeSpan.FromSeconds(1.2);

    // Below this peak no recording of any length contains audible speech (0.01 of full scale is
    // barely above the noise floor of a muted mic). Holding the hotkey for seconds without
    // speaking used to reach the transcriber, which then hallucinated text — often echoing the
    // custom-terms prompt verbatim.
    public static readonly float AbsoluteSilencePeakThreshold = 0.01f;

    // Prefix of the biasing prompt sent to transcription backends alongside the audio. On silent
    // or noisy input, transcription models tend to echo this prompt back as the "transcript".
    public const string CustomTermsPromptPrefix = "Eigennamen und Begriffe";

    public static string BuildCustomTermsPrompt(IReadOnlyList<string> customTerms) =>
        $"{CustomTermsPromptPrefix}: {string.Join(", ", customTerms)}";

    public static bool ShouldRejectRecording(TimeSpan duration) => duration < MinimumRecordingDuration;

    public static bool ShouldRejectRecording(TimeSpan duration, float peakAmplitude) =>
        ShouldRejectRecording(duration) ||
        peakAmplitude < AbsoluteSilencePeakThreshold ||
        (duration < SilenceCheckMaxDuration && peakAmplitude < SilencePeakThreshold);

    public static string CleanedTranscript(string text) => text.Trim();

    public static bool IsLikelyArtifact(string text, TimeSpan recordingDuration)
    {
        var cleaned = CleanedTranscript(text);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return true;
        }

        var words = cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var letters = cleaned.Count(char.IsLetter);

        if (letters == 0)
        {
            return true;
        }

        if (recordingDuration.TotalSeconds < 0.55 && (words.Length >= 5 || cleaned.Length >= 32))
        {
            return true;
        }

        if (recordingDuration.TotalSeconds < 0.8 && cleaned.Length >= 56)
        {
            return true;
        }

        return false;
    }

    public static bool IsLikelyArtifact(string text, TimeSpan recordingDuration, IReadOnlyList<string> customTerms) =>
        IsLikelyArtifact(text, recordingDuration) || IsPromptEcho(text, customTerms);

    // When no speech is on the recording, transcription models frequently return the biasing
    // prompt (or just the term list) as if the user had said it. Only relevant when custom terms
    // were actually sent along with the request.
    public static bool IsPromptEcho(string text, IReadOnlyList<string> customTerms)
    {
        if (customTerms.Count == 0)
        {
            return false;
        }

        var cleaned = CleanedTranscript(text);
        if (cleaned.StartsWith(CustomTermsPromptPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The transcript being exactly the configured term list (and nothing else) is an echo.
        // With a single term the user may genuinely have dictated just that word, so require two.
        return customTerms.Count >= 2 &&
               Normalize(cleaned) == Normalize(string.Join(" ", customTerms));
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
