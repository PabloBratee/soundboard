namespace Soundboard.Audio;

public enum AudioContainerType
{
    Wav,
    Mp3,
    Ogg
}

public enum AudioCodecType
{
    Pcm,
    MpegLayer3,
    Opus,
    Vorbis
}

public sealed record AudioFileFormat(
    AudioContainerType Container,
    AudioCodecType Codec,
    string OriginalExtension)
{
    public string DisplayLabel => (Container, Codec) switch
    {
        (AudioContainerType.Wav, _) => "WAV",
        (AudioContainerType.Mp3, _) => "MP3",
        (AudioContainerType.Ogg, AudioCodecType.Opus) => "OGG · Opus",
        (AudioContainerType.Ogg, AudioCodecType.Vorbis) => "OGG · Vorbis",
        _ => Container.ToString().ToUpperInvariant()
    };
}
