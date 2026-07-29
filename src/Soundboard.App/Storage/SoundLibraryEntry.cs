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
    string ContentHash);
