namespace Soundboard.Audio;

public static class AudioSamplePosition
{
    public static long TimeToFramePosition(
        TimeSpan position,
        int sampleRate)
    {
        if (position < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                "Audio positions cannot be negative.");
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                "The sample rate must be positive.");
        }

        var wholeSeconds = position.Ticks / TimeSpan.TicksPerSecond;
        var remainingTicks = position.Ticks % TimeSpan.TicksPerSecond;
        var wholeFrames = checked(wholeSeconds * sampleRate);
        var partialNumerator = checked(remainingTicks * sampleRate);
        var roundedPartialFrames = checked(
            (partialNumerator + (TimeSpan.TicksPerSecond / 2))
            / TimeSpan.TicksPerSecond);
        return checked(wholeFrames + roundedPartialFrames);
    }

    public static long FramesToInterleavedSamples(
        long frames,
        int channelCount)
    {
        if (frames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frames));
        }

        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }

        return checked(frames * channelCount);
    }
}
