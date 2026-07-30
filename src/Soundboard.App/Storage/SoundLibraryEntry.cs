using Soundboard.App.Hotkeys;
using Soundboard.Audio;

namespace Soundboard.App.Storage;

public sealed record SoundLibraryEntry(
    Guid Id,
    string DisplayName,
    string ManagedFileName,
    string OriginalFileName,
    string FileType,
    TimeSpan Duration,
    DateTimeOffset ImportedAtUtc,
    int SortOrder,
    string ContentHash,
    HotkeyGesture? Hotkey = null,
    Guid? CategoryId = null,
    bool IsFavorite = false,
    SoundTileAccent TileAccent = SoundTileAccent.Default,
    AudioContainerType Container = AudioContainerType.Wav,
    AudioCodecType Codec = AudioCodecType.Pcm,
    string OriginalExtension = ".wav")
{
    public string FormatLabel => new AudioFileFormat(
        Container,
        Codec,
        OriginalExtension).DisplayLabel;
}
