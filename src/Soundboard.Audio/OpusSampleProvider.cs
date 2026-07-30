using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;

namespace Soundboard.Audio;

internal sealed class OpusSampleProvider : ISampleProvider, IDisposable
{
    private const int OpusSampleRate = 48000;

    private readonly FileStream sourceStream;
    private readonly OpusDecoder decoder;
    private readonly OpusOggReadStream reader;
    private readonly int channels;
    private long samplesToSkip;
    private long samplesRemaining;
    private short[]? decodedPacket;
    private int decodedPacketOffset;
    private bool disposed;

    public OpusSampleProvider(
        string filePath,
        int channels,
        int preSkip,
        long playableFrames)
    {
        this.channels = channels;
        samplesToSkip = checked((long)preSkip * channels);
        samplesRemaining = checked(playableFrames * channels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            OpusSampleRate,
            channels);

        sourceStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
#pragma warning disable CS0618 // Explicitly use the managed decoder implementation.
        decoder = new OpusDecoder(OpusSampleRate, channels);
#pragma warning restore CS0618
        reader = new OpusOggReadStream(decoder, sourceStream);
        if (!reader.HasNextPacket)
        {
            Dispose();
            throw new InvalidDataException(
                "The Ogg Opus decoder could not initialize: "
                + (reader.LastError ?? "no audio packets were found."));
        }
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (count == 0 || samplesRemaining == 0)
        {
            return 0;
        }

        var written = 0;
        while (written < count && samplesRemaining > 0)
        {
            if (!EnsureDecodedPacket())
            {
                throw new InvalidDataException(
                    "The Ogg Opus stream ended before its declared "
                    + "duration.");
            }

            var available = decodedPacket!.Length - decodedPacketOffset;
            var take = (int)Math.Min(
                Math.Min(available, count - written),
                samplesRemaining);
            for (var index = 0; index < take; index++)
            {
                buffer[offset + written + index] =
                    decodedPacket[decodedPacketOffset + index] / 32768f;
            }

            decodedPacketOffset += take;
            written += take;
            samplesRemaining -= take;
        }

        return written;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        reader?.Close();
        decoder?.Dispose();
        sourceStream?.Dispose();
    }

    private bool EnsureDecodedPacket()
    {
        while (decodedPacket is null
            || decodedPacketOffset >= decodedPacket.Length)
        {
            decodedPacket = reader.DecodeNextPacket();
            decodedPacketOffset = 0;
            if (decodedPacket is null)
            {
                if (!string.IsNullOrWhiteSpace(reader.LastError))
                {
                    throw new InvalidDataException(reader.LastError);
                }

                return false;
            }

            if (samplesToSkip > 0)
            {
                var skip = (int)Math.Min(
                    samplesToSkip,
                    decodedPacket.Length);
                decodedPacketOffset = skip;
                samplesToSkip -= skip;
            }
        }

        return true;
    }
}
