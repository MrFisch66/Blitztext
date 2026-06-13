namespace Blitztext.Core.Services;

public static class TranscriptionQualityService
{
    public static readonly TimeSpan MinimumRecordingDuration = TimeSpan.FromSeconds(0.3);

    public static bool ShouldRejectRecording(TimeSpan duration) => duration < MinimumRecordingDuration;

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
}
