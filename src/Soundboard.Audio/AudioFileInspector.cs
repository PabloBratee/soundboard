using NAudio.Wave;

namespace Soundboard.Audio;

public static class AudioFileInspector
{
    public static AudioFileDetails Inspect(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var extension = Path.GetExtension(filePath);
        if (!extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Only WAV and MP3 audio files are supported.");
        }

        using var reader = new AudioFileReader(filePath);
        if (reader.TotalTime <= TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "The audio file does not contain playable audio.");
        }

        return new AudioFileDetails(
            reader.TotalTime,
            reader.WaveFormat.SampleRate,
            reader.WaveFormat.Channels);
    }
}

public sealed record AudioFileDetails(
    TimeSpan Duration,
    int SampleRate,
    int Channels);
