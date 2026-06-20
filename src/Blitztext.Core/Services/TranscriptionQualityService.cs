namespace Blitztext.Core.Services;

public static class TranscriptionQualityService
{
    public static readonly TimeSpan MinimumRecordingDuration = TimeSpan.FromSeconds(0.3);

    // A quick, accidental tap of the hotkey captures near-silence. Transcribers then hallucinate
    // filler phrases ("Vielen Dank.", "Untertitel …") out of it, which used to be pasted as if the
    // user had spoken. Treat a short recording whose loudest sample never crosses this floor as
    // "no speech" and drop it before transcription. The check is limited to short recordings so a
    // deliberately quiet but real utterance of normal length is still transcribed.
    public static readonly float SilencePeakThreshold = 0.02f;
    public static readonly TimeSpan SilenceCheckMaxDuration = TimeSpan.FromSeconds(1.2);

    public static bool ShouldRejectRecording(TimeSpan duration) => duration < MinimumRecordingDuration;

    public static bool ShouldRejectRecording(TimeSpan duration, float peakAmplitude) =>
        ShouldRejectRecording(duration) ||
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
}
