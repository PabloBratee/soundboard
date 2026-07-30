using NAudio.Wave;
using NVorbis;

namespace Soundboard.Audio;

internal sealed class VorbisSampleProvider : ISampleProvider, IDisposable
{
    private readonly VorbisReader reader;
    private bool disposed;

    public VorbisSampleProvider(Stream stream)
    {
        reader = new VorbisReader(stream, closeOnDispose: true);
        if (reader.Channels is < 1 or > 2)
        {
            Dispose();
            throw new NotSupportedException(
                $"Vorbis channel count {reader.Channels} is not supported. "
                + "Only mono and stereo audio are supported.");
        }

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            reader.SampleRate,
            reader.Channels);
    }

    public WaveFormat WaveFormat { get; }

    public TimeSpan TotalTime => reader.TotalTime;

    public int Read(float[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return reader.ReadSamples(buffer, offset, count);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        reader.Dispose();
    }
}
