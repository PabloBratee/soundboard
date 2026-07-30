using Soundboard.App.Hotkeys;

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
    SoundTileAccent TileAccent = SoundTileAccent.Default);
