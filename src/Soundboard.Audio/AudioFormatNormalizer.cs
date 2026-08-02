using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundboard.Audio;

internal static class AudioFormatNormalizer
{
    internal static readonly Guid PcmSubFormat =
        new("00000001-0000-0010-8000-00AA00389B71");

    internal static readonly Guid IeeeFloatSubFormat =
        new("00000003-0000-0010-8000-00AA00389B71");

    public static WaveFormat GetBufferedCaptureFormat(WaveFormat nativeFormat)
    {
        ArgumentNullException.ThrowIfNull(nativeFormat);

        if (nativeFormat.Channels is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"The microphone exposes {nativeFormat.Channels} channels. "
                + "Soundboard supports mono and stereo microphones only.");
        }

        if (nativeFormat.Encoding == WaveFormatEncoding.IeeeFloat
            && nativeFormat.BitsPerSample == 32)
        {
            return WaveFormat.CreateIeeeFloatWaveFormat(
                nativeFormat.SampleRate,
                nativeFormat.Channels);
        }

        if (nativeFormat.Encoding == WaveFormatEncoding.Pcm)
        {
            ValidatePcmBitDepth(nativeFormat.BitsPerSample);
            return new WaveFormat(
                nativeFormat.SampleRate,
                nativeFormat.BitsPerSample,
                nativeFormat.Channels);
        }

        if (nativeFormat is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat == IeeeFloatSubFormat
                && nativeFormat.BitsPerSample == 32)
            {
                return WaveFormat.CreateIeeeFloatWaveFormat(
                    nativeFormat.SampleRate,
                    nativeFormat.Channels);
            }

            if (extensible.SubFormat == PcmSubFormat)
            {
                ValidatePcmBitDepth(nativeFormat.BitsPerSample);
                return new WaveFormat(
                    nativeFormat.SampleRate,
                    nativeFormat.BitsPerSample,
                    nativeFormat.Channels);
            }
        }

        throw new NotSupportedException(
            $"The microphone format is not supported: {nativeFormat}.");
    }

    public static ISampleProvider Normalize(
        ISampleProvider source,
        WaveFormat targetFormat,
        out bool resamplingActive,
        out bool channelConversionActive)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targetFormat);

        if (source.WaveFormat.Channels is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"The source exposes {source.WaveFormat.Channels} channels. "
                + "Only mono and stereo sources are supported.");
        }

        if (targetFormat.Channels is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"The selected output exposes {targetFormat.Channels} channels. "
                + "Soundboard supports mono and stereo output only.");
        }

        ISampleProvider normalized = source;
        channelConversionActive = source.WaveFormat.Channels != targetFormat.Channels;

        if (source.WaveFormat.Channels == 1 && targetFormat.Channels == 2)
        {
            normalized = new MonoToStereoSampleProvider(normalized);
        }
        else if (source.WaveFormat.Channels == 2 && targetFormat.Channels == 1)
        {
            normalized = new StereoToMonoSampleProvider(normalized)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
        }

        resamplingActive = normalized.WaveFormat.SampleRate != targetFormat.SampleRate;

        if (resamplingActive)
        {
            normalized = new WdlResamplingSampleProvider(
                normalized,
                targetFormat.SampleRate);
        }

        return normalized;
    }

    private static void ValidatePcmBitDepth(int bitsPerSample)
    {
        if (bitsPerSample is not (16 or 24 or 32))
        {
            throw new NotSupportedException(
                $"The microphone uses unsupported {bitsPerSample}-bit PCM audio.");
        }
    }
}
