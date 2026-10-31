using System.Buffers.Binary;

namespace Blitztext.Core.Services;

/// <summary>
/// Streaming 16 kHz → 24 kHz PCM16 resampler (linear interpolation). The OpenAI realtime API only
/// accepts 24 kHz input, while the recorder captures 16 kHz for the local models. State carries
/// across chunks so chunk boundaries do not produce clicks or drift.
/// </summary>
public sealed class PcmResampler
{
    private short _previousSample;
    private long _inputCount;
    private long _outputCount;

    public byte[] Resample16To24(ReadOnlySpan<byte> pcm16)
    {
        var sampleCount = pcm16.Length / 2;
        if (sampleCount == 0)
        {
            return [];
        }

        var lastAvailable = _inputCount + sampleCount - 1;
        var output = new List<short>(sampleCount * 3 / 2 + 2);

        while (true)
        {
            // Output sample j sits at input position j * 2/3.
            var numerator = _outputCount * 2;
            var index0 = numerator / 3;
            var fraction = (int)(numerator % 3);
            var index1 = fraction == 0 ? index0 : index0 + 1;
            if (index1 > lastAvailable)
            {
                break;
            }

            var sample0 = SampleAt(pcm16, index0);
            var sample1 = SampleAt(pcm16, index1);
            output.Add((short)(sample0 + (sample1 - sample0) * fraction / 3));
            _outputCount++;
        }

        _previousSample = BinaryPrimitives.ReadInt16LittleEndian(pcm16[((sampleCount - 1) * 2)..]);
        _inputCount += sampleCount;

        var bytes = new byte[output.Count * 2];
        for (var index = 0; index < output.Count; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2), output[index]);
        }

        return bytes;
    }

    private short SampleAt(ReadOnlySpan<byte> pcm16, long globalIndex)
    {
        var local = globalIndex - _inputCount;
        return local < 0
            ? _previousSample
            : BinaryPrimitives.ReadInt16LittleEndian(pcm16[(int)(local * 2)..]);
    }
}
