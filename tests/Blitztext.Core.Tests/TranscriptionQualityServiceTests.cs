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
    public void IsLikelyArtifact_RejectsLongTextFromTinyRecording()
    {
        var result = TranscriptionQualityService.IsLikelyArtifact(
            "Dies ist ein viel zu langer Text für eine halbe Sekunde",
            TimeSpan.FromMilliseconds(400));

        Assert.True(result);
    }
}
