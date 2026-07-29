using NAudio.Wave;

namespace Soundboard.Audio;

public sealed record AudioFormatInfo(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    string SampleFormat)
{
    internal static AudioFormatInfo FromWaveFormat(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        return new AudioFormatInfo(
            format.SampleRate,
            format.Channels,
            format.BitsPerSample,
            DescribeSampleFormat(format));
    }

    public override string ToString()
    {
        var channelText = Channels == 1 ? "mono" : $"{Channels} channels";
        return $"{SampleRate:N0} Hz, {channelText}, {BitsPerSample}-bit {SampleFormat}";
    }

    private static string DescribeSampleFormat(WaveFormat format)
    {
        if (format is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat == AudioFormatNormalizer.IeeeFloatSubFormat)
            {
                return "IEEE float (extensible)";
            }

            if (extensible.SubFormat == AudioFormatNormalizer.PcmSubFormat)
            {
                return "PCM (extensible)";
            }

            return $"extensible ({extensible.SubFormat})";
        }

        return format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => "IEEE float",
            WaveFormatEncoding.Pcm => "PCM",
            _ => format.Encoding.ToString()
        };
    }
}
