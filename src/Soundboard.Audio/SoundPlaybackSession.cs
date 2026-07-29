using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundboard.Audio;

internal sealed class SoundPlaybackSession : ISampleProvider, IDisposable
{
    private readonly object syncRoot = new();
    private readonly AudioFileReader reader;
    private readonly VolumeSampleProvider volumeProvider;
    private Exception? playbackError;
    private bool disposed;
    private int completionQueued;

    public SoundPlaybackSession(
        Guid soundId,
        long sessionId,
        string filePath,
        WaveFormat targetFormat,
        float volume)
    {
        SoundId = soundId;
        SessionId = sessionId;
        reader = new AudioFileReader(filePath);

        try
        {
            var normalized = AudioFormatNormalizer.Normalize(
                reader,
                targetFormat,
                out _,
                out _);

            volumeProvider = new VolumeSampleProvider(normalized)
            {
                Volume = volume
            };
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    public WaveFormat WaveFormat => volumeProvider.WaveFormat;

    public Guid SoundId { get; }

    public long SessionId { get; }

    public Exception? PlaybackError
    {
        get
        {
            lock (syncRoot)
            {
                return playbackError;
            }
        }
    }

    public float Volume
    {
        get
        {
            lock (syncRoot)
            {
                return volumeProvider.Volume;
            }
        }

        set
        {
            lock (syncRoot)
            {
                volumeProvider.Volume = value;
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var samplesRead = 0;

        lock (syncRoot)
        {
            if (disposed)
            {
                return 0;
            }

            try
            {
                samplesRead = volumeProvider.Read(buffer, offset, count);
            }
            catch (Exception exception)
            {
                playbackError = exception;
            }
        }

        return samplesRead;
    }

    public bool TryQueueCompletion()
    {
        return Interlocked.Exchange(ref completionQueued, 1) == 0;
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            reader.Dispose();
        }
    }
}
