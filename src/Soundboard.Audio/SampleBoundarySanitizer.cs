using NAudio.Wave;

namespace Soundboard.Audio;

/// <summary>
/// Final-boundary integrity guard. It leaves every valid in-range sample
/// unchanged and only replaces non-finite values or clips values outside the
/// representable floating-point PCM range.
/// </summary>
public sealed class SampleBoundarySanitizer : ISampleProvider
{
    private readonly ISampleProvider source;
    private long clippedSampleCount;
    private long nonFiniteSampleCount;

    public SampleBoundarySanitizer(ISampleProvider source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public long ClippedSampleCount => Interlocked.Read(ref clippedSampleCount);

    public long NonFiniteSampleCount => Interlocked.Read(ref nonFiniteSampleCount);

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        for (var index = offset; index < offset + read; index++)
        {
            var sample = buffer[index];
            if (!float.IsFinite(sample))
            {
                buffer[index] = 0f;
                Interlocked.Increment(ref nonFiniteSampleCount);
            }
            else if (sample > 1f)
            {
                buffer[index] = 1f;
                Interlocked.Increment(ref clippedSampleCount);
            }
            else if (sample < -1f)
            {
                buffer[index] = -1f;
                Interlocked.Increment(ref clippedSampleCount);
            }
        }

        return read;
    }
}
