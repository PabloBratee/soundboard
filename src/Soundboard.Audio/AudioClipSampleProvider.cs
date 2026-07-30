using NAudio.Wave;

namespace Soundboard.Audio;

public sealed class AudioClipSampleProvider : ISampleProvider
{
    private const int SkipBufferLength = 8192;

    private readonly ISampleProvider source;
    private readonly int channelCount;
    private readonly long trimStartSamples;
    private readonly long clipFrames;
    private readonly long fadeInFrames;
    private readonly long fadeOutFrames;
    private readonly float[] skipBuffer = new float[SkipBufferLength];
    private long emittedSamples;
    private bool trimStartSkipped;
    private bool ended;

    public AudioClipSampleProvider(
        ISampleProvider source,
        AudioClipSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        if (source.WaveFormat.Channels is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"The source exposes {source.WaveFormat.Channels} channels. "
                + "Only mono and stereo clip editing is supported.");
        }

        this.source = source;
        WaveFormat = source.WaveFormat;
        channelCount = WaveFormat.Channels;

        var startFrame = AudioSamplePosition.TimeToFramePosition(
            settings.TrimStart,
            WaveFormat.SampleRate);
        var endFrame = AudioSamplePosition.TimeToFramePosition(
            settings.TrimEnd,
            WaveFormat.SampleRate);
        if (endFrame <= startFrame)
        {
            throw new ArgumentException(
                "The trim range does not contain a decoded sample frame.",
                nameof(settings));
        }

        trimStartSamples = AudioSamplePosition.FramesToInterleavedSamples(
            startFrame,
            channelCount);
        clipFrames = checked(endFrame - startFrame);
        RemainingSamples = AudioSamplePosition.FramesToInterleavedSamples(
            clipFrames,
            channelCount);
        fadeInFrames = Math.Min(
            clipFrames,
            AudioSamplePosition.TimeToFramePosition(
                settings.FadeIn,
                WaveFormat.SampleRate));
        fadeOutFrames = Math.Min(
            clipFrames,
            AudioSamplePosition.TimeToFramePosition(
                settings.FadeOut,
                WaveFormat.SampleRate));
    }

    public WaveFormat WaveFormat { get; }

    public long RemainingSamples { get; private set; }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "The requested buffer range is invalid.");
        }

        if (count == 0 || ended)
        {
            return 0;
        }

        if (!trimStartSkipped)
        {
            SkipTrimmedStart();
            if (ended)
            {
                return 0;
            }
        }

        var requested = (int)Math.Min(count, RemainingSamples);
        if (requested <= 0)
        {
            ended = true;
            return 0;
        }

        var read = source.Read(buffer, offset, requested);
        if (read <= 0)
        {
            RemainingSamples = 0;
            ended = true;
            return 0;
        }

        for (var index = 0; index < read; index++)
        {
            var frameIndex = (emittedSamples + index) / channelCount;
            buffer[offset + index] *= GetGain(frameIndex);
        }

        emittedSamples = checked(emittedSamples + read);
        RemainingSamples -= read;
        if (RemainingSamples == 0)
        {
            ended = true;
        }

        return read;
    }

    private void SkipTrimmedStart()
    {
        long skipped = 0;
        while (skipped < trimStartSamples)
        {
            var requested = (int)Math.Min(
                skipBuffer.Length,
                trimStartSamples - skipped);
            var read = source.Read(skipBuffer, 0, requested);
            if (read <= 0)
            {
                RemainingSamples = 0;
                ended = true;
                break;
            }

            skipped = checked(skipped + read);
        }

        trimStartSkipped = true;
    }

    private float GetGain(long frameIndex)
    {
        var gain = 1d;
        if (fadeInFrames > 0 && frameIndex < fadeInFrames)
        {
            gain = fadeInFrames == 1
                ? 0d
                : frameIndex / (double)(fadeInFrames - 1);
        }

        var framesThroughEnd = clipFrames - frameIndex;
        if (fadeOutFrames > 0 && framesThroughEnd <= fadeOutFrames)
        {
            var fadeOutGain = fadeOutFrames == 1
                ? 0d
                : (framesThroughEnd - 1d) / (fadeOutFrames - 1d);
            gain = Math.Min(gain, fadeOutGain);
        }

        return (float)Math.Clamp(gain, 0d, 1d);
    }
}
