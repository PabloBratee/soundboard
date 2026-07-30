namespace Soundboard.Audio;

public static class AudioFileInspector
{
    private static readonly TimeSpan MaximumDuration =
        TimeSpan.FromHours(12);

    public static AudioFileDetails Inspect(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        try
        {
            using var source = AudioFileDecoderFactory.Default.Open(filePath);
            ValidateDuration(source.Duration);
            if (source.Duration < AudioClipSettings.MinimumPlayableDuration)
            {
                throw new InvalidDataException(
                    "The audio file is shorter than the 100-millisecond "
                    + "minimum playable clip duration.");
            }

            var validationBuffer = new float[
                Math.Max(source.ChannelCount, Math.Min(
                    source.SampleRate * source.ChannelCount / 100,
                    4096))];
            var decodedSamples = source.SampleProvider.Read(
                validationBuffer,
                0,
                validationBuffer.Length);
            if (decodedSamples <= 0)
            {
                throw new InvalidDataException(
                    "The audio file does not contain decodable audio "
                    + "samples.");
            }

            return new AudioFileDetails(
                source.Duration,
                source.SampleRate,
                source.ChannelCount,
                source.Format);
        }
        catch (Exception exception)
            when (exception is not (
                IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or InvalidDataException))
        {
            throw new InvalidDataException(
                "The audio decoder could not read this file: "
                + exception.Message,
                exception);
        }
    }

    internal static void ValidateDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > MaximumDuration)
        {
            throw new InvalidDataException(
                duration <= TimeSpan.Zero
                    ? "The audio file has an impossible or empty duration."
                    : $"The audio file exceeds the "
                        + $"{MaximumDuration.TotalHours:0}-hour import "
                        + "duration limit.");
        }
    }
}

public sealed record AudioFileDetails(
    TimeSpan Duration,
    int SampleRate,
    int Channels,
    AudioFileFormat Format);
