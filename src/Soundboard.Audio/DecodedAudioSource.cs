using NAudio.Wave;

namespace Soundboard.Audio;

public sealed class DecodedAudioSource : IDisposable
{
    private readonly IDisposable decoderResources;
    private readonly Func<DecodedAudioSource> restartFactory;
    private bool disposed;

    internal DecodedAudioSource(
        string sourceFileName,
        ISampleProvider sampleProvider,
        TimeSpan duration,
        AudioFileFormat format,
        IDisposable decoderResources,
        Func<DecodedAudioSource> restartFactory)
    {
        SourceFileName = sourceFileName;
        SampleProvider = sampleProvider;
        Duration = duration;
        Format = format;
        this.decoderResources = decoderResources;
        this.restartFactory = restartFactory;
    }

    public string SourceFileName { get; }

    public ISampleProvider SampleProvider { get; }

    public int SampleRate => SampleProvider.WaveFormat.SampleRate;

    public int ChannelCount => SampleProvider.WaveFormat.Channels;

    public TimeSpan Duration { get; }

    public AudioFileFormat Format { get; }

    public bool CanRestart => true;

    public DecodedAudioSource Restart()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Dispose();
        return restartFactory();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        decoderResources.Dispose();
    }
}

public interface IAudioFileDecoderFactory
{
    DecodedAudioSource Open(string filePath);
}
