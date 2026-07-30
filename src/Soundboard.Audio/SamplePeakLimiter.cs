using NAudio.Wave;

namespace Soundboard.Audio;

public sealed class SamplePeakLimiter : ISampleProvider
{
    public const double DefaultCeilingDbfs = -1d;
    public const double MinimumCeilingDbfs = -6d;
    public const double MaximumCeilingDbfs = -0.1d;
    public static readonly TimeSpan DefaultLookahead =
        TimeSpan.FromMilliseconds(5);
    public static readonly TimeSpan DefaultRelease =
        TimeSpan.FromMilliseconds(100);

    private const int ScratchFrames = 4096;

    private readonly ISampleProvider source;
    private readonly int channels;
    private readonly int lookaheadFrames;
    private readonly int capacityFrames;
    private readonly float[] delayBuffer;
    private readonly float[] framePeaks;
    private readonly float[] sourceScratch;
    private readonly double releaseCoefficient;
    private int ceilingBits;
    private int enabledValue;
    private int activeEnabledValue;
    private int writeFrame;
    private int readFrame;
    private int bufferedFrames;
    private bool sourceEnded;
    private float currentGain = 1f;
    private float currentGainReductionDb;
    private float maximumGainReductionDb;
    private long nonFiniteSampleCount;

    public SamplePeakLimiter(
        ISampleProvider source,
        bool enabled = true,
        double ceilingDbfs = DefaultCeilingDbfs,
        TimeSpan? lookahead = null,
        TimeSpan? release = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.WaveFormat.Channels is < 1 or > 2)
        {
            throw new NotSupportedException(
                "The sample-peak limiter supports mono and stereo only.");
        }

        ValidateCeiling(ceilingDbfs);
        var actualLookahead = lookahead ?? DefaultLookahead;
        var actualRelease = release ?? DefaultRelease;
        if (actualLookahead < TimeSpan.Zero
            || actualLookahead > TimeSpan.FromMilliseconds(20))
        {
            throw new ArgumentOutOfRangeException(nameof(lookahead));
        }

        if (actualRelease <= TimeSpan.Zero
            || actualRelease > TimeSpan.FromSeconds(2))
        {
            throw new ArgumentOutOfRangeException(nameof(release));
        }

        this.source = source;
        WaveFormat = source.WaveFormat;
        channels = WaveFormat.Channels;
        lookaheadFrames = Math.Max(
            0,
            (int)Math.Round(
                actualLookahead.TotalSeconds * WaveFormat.SampleRate));
        capacityFrames = lookaheadFrames + 1;
        delayBuffer = new float[capacityFrames * channels];
        framePeaks = new float[capacityFrames];
        sourceScratch = new float[ScratchFrames * channels];
        releaseCoefficient = Math.Exp(
            -1d
            / (actualRelease.TotalSeconds * WaveFormat.SampleRate));
        ceilingBits = BitConverter.SingleToInt32Bits(
            DbfsToLinear(ceilingDbfs));
        enabledValue = enabled ? 1 : 0;
        activeEnabledValue = enabledValue;
        AddedLatency = TimeSpan.FromSeconds(
            lookaheadFrames / (double)WaveFormat.SampleRate);
        Release = actualRelease;
    }

    public WaveFormat WaveFormat { get; }

    public TimeSpan AddedLatency { get; }

    public TimeSpan Release { get; }

    public bool Enabled
    {
        get => Volatile.Read(ref enabledValue) != 0;
        set => Volatile.Write(ref enabledValue, value ? 1 : 0);
    }

    public double CeilingDbfs
    {
        get => LinearToDbfs(
            BitConverter.Int32BitsToSingle(
                Volatile.Read(ref ceilingBits)));
        set
        {
            ValidateCeiling(value);
            Volatile.Write(
                ref ceilingBits,
                BitConverter.SingleToInt32Bits(DbfsToLinear(value)));
        }
    }

    public float CurrentGainReductionDb =>
        Volatile.Read(ref currentGainReductionDb);

    public float MaximumGainReductionDb =>
        Volatile.Read(ref maximumGainReductionDb);

    public long NonFiniteSampleCount =>
        Interlocked.Read(ref nonFiniteSampleCount);

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var requested = count - (count % channels);
        if (requested <= 0)
        {
            return 0;
        }

        var enabled = Volatile.Read(ref enabledValue);
        if (enabled != activeEnabledValue)
        {
            if (activeEnabledValue != 0 && enabled == 0)
            {
                var drained = 0;
                while (bufferedFrames > 0 && drained < requested)
                {
                    EmitFrame(buffer, offset + drained);
                    drained += channels;
                }

                if (bufferedFrames == 0)
                {
                    activeEnabledValue = 0;
                    currentGain = 1f;
                    Volatile.Write(ref currentGainReductionDb, 0f);
                    if (drained < requested)
                    {
                        drained += source.Read(
                            buffer,
                            offset + drained,
                            requested - drained);
                    }
                }

                return drained;
            }

            ResetDelayState(resetMaximum: false);
            sourceEnded = false;
            activeEnabledValue = enabled;
        }

        if (enabled == 0)
        {
            return source.Read(buffer, offset, requested);
        }

        var outputSamples = 0;
        while (outputSamples < requested)
        {
            if (!sourceEnded)
            {
                var neededFrames =
                    (requested - outputSamples) / channels;
                var primingFrames = Math.Max(
                    0,
                    lookaheadFrames - bufferedFrames);
                var readFrames = Math.Min(
                    ScratchFrames,
                    neededFrames + primingFrames);
                var read = source.Read(
                    sourceScratch,
                    0,
                    readFrames * channels);
                var alignedRead = read - (read % channels);
                if (alignedRead <= 0)
                {
                    sourceEnded = true;
                }
                else
                {
                    for (var index = 0;
                         index < alignedRead;
                         index += channels)
                    {
                        IngestFrame(sourceScratch, index);
                        if (bufferedFrames == capacityFrames
                            && outputSamples < requested)
                        {
                            EmitFrame(buffer, offset + outputSamples);
                            outputSamples += channels;
                        }
                    }
                }
            }

            if (sourceEnded)
            {
                while (bufferedFrames > 0 && outputSamples < requested)
                {
                    EmitFrame(buffer, offset + outputSamples);
                    outputSamples += channels;
                }

                break;
            }
        }

        return outputSamples;
    }

    public void Reset()
    {
        ResetDelayState(resetMaximum: true);
        sourceEnded = false;
    }

    private void IngestFrame(float[] buffer, int offset)
    {
        var peak = 0f;
        var targetOffset = writeFrame * channels;
        for (var channel = 0; channel < channels; channel++)
        {
            var sample = buffer[offset + channel];
            if (!float.IsFinite(sample))
            {
                sample = 0f;
                Interlocked.Increment(ref nonFiniteSampleCount);
            }

            delayBuffer[targetOffset + channel] = sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }

        framePeaks[writeFrame] = peak;
        writeFrame = (writeFrame + 1) % capacityFrames;
        bufferedFrames++;
    }

    private void EmitFrame(float[] buffer, int offset)
    {
        var windowPeak = 0f;
        var index = readFrame;
        for (var frame = 0; frame < bufferedFrames; frame++)
        {
            windowPeak = Math.Max(windowPeak, framePeaks[index]);
            index = (index + 1) % capacityFrames;
        }

        var ceiling = BitConverter.Int32BitsToSingle(
            Volatile.Read(ref ceilingBits));
        var targetGain = windowPeak > ceiling && windowPeak > 0f
            ? ceiling / windowPeak
            : 1f;
        if (targetGain < currentGain)
        {
            currentGain = targetGain;
        }
        else
        {
            currentGain = (float)(
                targetGain
                + releaseCoefficient * (currentGain - targetGain));
        }

        var sourceOffset = readFrame * channels;
        for (var channel = 0; channel < channels; channel++)
        {
            var output = delayBuffer[sourceOffset + channel] * currentGain;
            buffer[offset + channel] = float.IsFinite(output)
                ? Math.Clamp(output, -ceiling, ceiling)
                : 0f;
        }

        var reduction = currentGain >= 1f
            ? 0f
            : (float)(-20d * Math.Log10(currentGain));
        Volatile.Write(ref currentGainReductionDb, reduction);
        if (reduction > Volatile.Read(ref maximumGainReductionDb))
        {
            Volatile.Write(ref maximumGainReductionDb, reduction);
        }

        framePeaks[readFrame] = 0f;
        readFrame = (readFrame + 1) % capacityFrames;
        bufferedFrames--;
    }

    private void ResetDelayState(bool resetMaximum)
    {
        Array.Clear(delayBuffer);
        Array.Clear(framePeaks);
        writeFrame = 0;
        readFrame = 0;
        bufferedFrames = 0;
        currentGain = 1f;
        Volatile.Write(ref currentGainReductionDb, 0f);
        if (resetMaximum)
        {
            Volatile.Write(ref maximumGainReductionDb, 0f);
        }
    }

    private static float DbfsToLinear(double value)
    {
        return (float)Math.Pow(10d, value / 20d);
    }

    private static double LinearToDbfs(float value)
    {
        return 20d * Math.Log10(value);
    }

    private static void ValidateCeiling(double value)
    {
        if (!double.IsFinite(value)
            || value < MinimumCeilingDbfs
            || value > MaximumCeilingDbfs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "The limiter ceiling must be between -6.0 and -0.1 dBFS.");
        }
    }
}
