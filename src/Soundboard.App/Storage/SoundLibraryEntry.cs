using Soundboard.App.Hotkeys;
using Soundboard.Audio;
using System.Text.Json.Serialization;

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
    string OriginalExtension = ".wav",
    int TrimStartMilliseconds = 0,
    int? TrimEndMilliseconds = null,
    int FadeInMilliseconds = 0,
    int FadeOutMilliseconds = 0)
{
    [JsonIgnore]
    public string FormatLabel => new AudioFileFormat(
        Container,
        Codec,
        OriginalExtension).DisplayLabel;

    [JsonIgnore]
    public AudioClipSettings ClipSettings =>
        TrimStartMilliseconds == 0
        && TrimEndMilliseconds is null
        && FadeInMilliseconds == 0
        && FadeOutMilliseconds == 0
            ? AudioClipSettings.FullDuration(Duration)
            : AudioClipSettings.Create(
                Duration,
                TrimStartMilliseconds,
                TrimEndMilliseconds,
                FadeInMilliseconds,
                FadeOutMilliseconds);

    [JsonIgnore]
    public TimeSpan EffectiveDuration => ClipSettings.EffectiveDuration;

    [JsonIgnore]
    public bool HasClipEdits => ClipSettings.IsEdited;
}
