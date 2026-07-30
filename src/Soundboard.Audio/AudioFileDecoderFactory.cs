using NAudio.Wave;

namespace Soundboard.Audio;

public sealed class AudioFileDecoderFactory : IAudioFileDecoderFactory
{
    public static AudioFileDecoderFactory Default { get; } = new();

    public DecodedAudioSource Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3" or ".ogg" or ".opus"))
        {
            throw new NotSupportedException(
                "Only WAV, MP3, Ogg Opus, and Ogg Vorbis audio files are "
                + "supported.");
        }

        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException(
                "The audio file no longer exists.",
                fullPath);
        }

        if (fileInfo.Length == 0)
        {
            throw new InvalidDataException("The audio file is empty.");
        }

        return extension is ".ogg" or ".opus"
            ? OpenOgg(fullPath, extension)
            : OpenWithNAudio(fullPath, extension);
    }

    private DecodedAudioSource OpenWithNAudio(
        string filePath,
        string extension)
    {
        AudioFileReader? reader = null;
        try
        {
            reader = new AudioFileReader(filePath);
            AudioFileInspector.ValidateDuration(reader.TotalTime);
            var format = extension == ".wav"
                ? new AudioFileFormat(
                    AudioContainerType.Wav,
                    AudioCodecType.Pcm,
                    extension)
                : new AudioFileFormat(
                    AudioContainerType.Mp3,
                    AudioCodecType.MpegLayer3,
                    extension);
            return new DecodedAudioSource(
                Path.GetFileName(filePath),
                reader,
                reader.TotalTime,
                format,
                reader,
                () => Open(filePath));
        }
        catch
        {
            reader?.Dispose();
            throw;
        }
    }

    private DecodedAudioSource OpenOgg(
        string filePath,
        string extension)
    {
        OggStreamInfo streamInfo;
        using (var inspectionStream = new FileStream(
                   filePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 81920,
                   FileOptions.SequentialScan))
        {
            streamInfo = OggContainerInspector.Inspect(inspectionStream);
        }

        var format = new AudioFileFormat(
            AudioContainerType.Ogg,
            streamInfo.Codec,
            extension);
        if (streamInfo.Codec == AudioCodecType.Opus)
        {
            var playableFrames = checked(
                streamInfo.FinalGranulePosition
                - streamInfo.OpusPreSkip);
            var duration = TimeSpan.FromSeconds(
                playableFrames / 48000d);
            AudioFileInspector.ValidateDuration(duration);
            var provider = new OpusSampleProvider(
                filePath,
                streamInfo.Channels,
                streamInfo.OpusPreSkip,
                playableFrames);
            return new DecodedAudioSource(
                Path.GetFileName(filePath),
                provider,
                duration,
                format,
                provider,
                () => Open(filePath));
        }

        var sourceStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        VorbisSampleProvider? vorbis = null;
        try
        {
            vorbis = new VorbisSampleProvider(sourceStream);
            if (vorbis.WaveFormat.SampleRate != streamInfo.SampleRate
                || vorbis.WaveFormat.Channels != streamInfo.Channels)
            {
                throw new InvalidDataException(
                    "The Ogg Vorbis decoder disagrees with the "
                    + "identification header.");
            }

            AudioFileInspector.ValidateDuration(vorbis.TotalTime);
            return new DecodedAudioSource(
                Path.GetFileName(filePath),
                vorbis,
                vorbis.TotalTime,
                format,
                vorbis,
                () => Open(filePath));
        }
        catch
        {
            if (vorbis is not null)
            {
                vorbis.Dispose();
            }
            else
            {
                sourceStream.Dispose();
            }

            throw;
        }
    }
}
