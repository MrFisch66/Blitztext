using Blitztext.Core.Services;

namespace Blitztext.Core.Tests;

public sealed class TranscriptionQualityServiceTests
{
    [Fact]
    public void ShouldRejectRecording_RejectsVeryShortRecording()
    {
        Assert.True(TranscriptionQualityService.ShouldRejectRecording(TimeSpan.FromMilliseconds(250)));
        Assert.False(TranscriptionQualityService.ShouldRejectRecording(TimeSpan.FromMilliseconds(350)));
    }

    [Fact]
    public void ShouldRejectRecording_RejectsShortSilentRecording()
    {
        // A quick hotkey tap: long enough to pass the duration floor, but effectively silent.
        Assert.True(TranscriptionQualityService.ShouldRejectRecording(TimeSpan.FromMilliseconds(600), peakAmplitude: 0.005f));
    }

    [Fact]
    public void ShouldRejectRecording_AcceptsShortRecordingWithSpeech()
    {
        Assert.False(TranscriptionQualityService.ShouldRejectRecording(TimeSpan.FromMilliseconds(600), peakAmplitude: 0.3f));
    }

    [Fact]
    public void ShouldRejectRecording_AcceptsLongRecordingEvenWhenQuiet()
    {
        // Beyond the short-recording window we trust the audio rather than risk dropping real speech.
        Assert.False(TranscriptionQualityService.ShouldRejectRecording(TimeSpan.FromSeconds(3), peakAmplitude: 0.005f));
    }

    [Fact]
    public void IsLikelyArtifact_RejectsLongTextFromTinyRecording()
    {
        var result = TranscriptionQualityService.IsLikelyArtifact(
            "Dies ist ein viel zu langer Text für eine halbe Sekunde",
            TimeSpan.FromMilliseconds(400));

        Assert.True(result);
    }
}
