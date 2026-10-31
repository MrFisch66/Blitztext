using System.Buffers.Binary;
using Blitztext.Core.Services;

namespace Blitztext.Core.Tests;

public sealed class LiveTranscriptionTests
{
    [Fact]
    public void Resampler_ProducesThreeHalvesOfTheSamples_AcrossChunks()
    {
        var resampler = new PcmResampler();
        var total = 0;
        for (var chunk = 0; chunk < 10; chunk++)
        {
            total += resampler.Resample16To24(Pcm(Enumerable.Repeat((short)1000, 800).ToArray())).Length / 2;
        }

        // 8000 input samples → 12000 output samples (the last one or two wait for the next chunk).
        Assert.InRange(total, 11_998, 12_000);
    }

    [Fact]
    public void Resampler_InterpolatesLinearly_OverChunkBoundary()
    {
        var resampler = new PcmResampler();
        var first = Samples(resampler.Resample16To24(Pcm([0, 300])));
        var second = Samples(resampler.Resample16To24(Pcm([600, 900])));

        Assert.Equal(new short[] { 0, 200 }, first);
        Assert.Equal(new short[] { 400, 600, 800 }, second);
    }

    [Fact]
    public void Keywords_StripCharactersTheApiRejects()
    {
        var keywords = OpenAITranscriptionOptions.Keywords(["Blitz<text>", "Claude\nCode", " ", "blitztext"]);

        Assert.Equal(new[] { "Blitz text", "Claude Code", "blitztext" }, keywords);
    }

    [Theory]
    [InlineData("de", new[] { "de" })]
    [InlineData(" DE ", new[] { "de" })]
    [InlineData("auto", new string[0])]
    [InlineData("", new string[0])]
    public void Languages_MapAutoAndEmptyToDetection(string language, string[] expected)
    {
        Assert.Equal(expected, OpenAITranscriptionOptions.Languages(language));
    }

    private static byte[] Pcm(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2), samples[index]);
        }

        return bytes;
    }

    private static short[] Samples(byte[] pcm) =>
        Enumerable.Range(0, pcm.Length / 2)
            .Select(index => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * 2)))
            .ToArray();
}
