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
    public void ShouldRejectRecording_RejectsLongSilentRecording()
    {
        // Holding the hotkey for seconds without speaking must not reach the transcriber.
        Assert.True(TranscriptionQualityService.ShouldRejectRecording(TimeSpan.FromSeconds(3), peakAmplitude: 0.005f));
    }

    [Fact]
    public void ShouldRejectRecording_AcceptsLongQuietRecordingWithSpeech()
    {
        // Quiet but real speech (low mic gain) stays above the absolute-silence floor.
        Assert.False(TranscriptionQualityService.ShouldRejectRecording(TimeSpan.FromSeconds(3), peakAmplitude: 0.015f));
    }

    [Fact]
    public void IsLikelyArtifact_RejectsLongTextFromTinyRecording()
    {
        var result = TranscriptionQualityService.IsLikelyArtifact(
            "Dies ist ein viel zu langer Text für eine halbe Sekunde",
            TimeSpan.FromMilliseconds(400));

        Assert.True(result);
    }

    [Fact]
    public void IsPromptEcho_DetectsEchoedPrompt()
    {
        var terms = new[] { "Codex", "Claude Code" };

        Assert.True(TranscriptionQualityService.IsPromptEcho("Eigennamen und Begriffe: Codex, Claude Code", terms));
        Assert.True(TranscriptionQualityService.IsPromptEcho("eigennamen und begriffe:", terms));
    }

    [Fact]
    public void IsPromptEcho_DetectsBareTermListEcho()
    {
        var terms = new[] { "Codex", "Claude Code" };

        Assert.True(TranscriptionQualityService.IsPromptEcho("Codex, Claude Code.", terms));
    }

    [Fact]
    public void IsPromptEcho_AcceptsRealDictation()
    {
        var terms = new[] { "Codex", "Claude Code" };

        Assert.False(TranscriptionQualityService.IsPromptEcho("Ich habe das mit Claude Code umgesetzt.", terms));
        // A single configured term may legitimately be dictated on its own.
        Assert.False(TranscriptionQualityService.IsPromptEcho("GrumBuddy", ["GrumBuddy"]));
        // Without configured terms no prompt was sent, so nothing can be an echo.
        Assert.False(TranscriptionQualityService.IsPromptEcho("Eigennamen und Begriffe sind wichtig.", []));
    }
}
