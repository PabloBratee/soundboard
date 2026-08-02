namespace Soundboard.App.Storage;

public sealed record SoundLibraryLoadResult(
    IReadOnlyList<SoundLibraryEntry> Sounds,
    IReadOnlyList<SoundCategory> Categories,
    IReadOnlyList<string> Warnings);

public sealed record SoundMetadataUpdate(
    string DisplayName,
    Guid? CategoryId,
    bool IsFavorite,
    SoundTileAccent TileAccent,
    double VolumePercent);

public sealed record SoundClipMetadataUpdate(
    int TrimStartMilliseconds,
    int? TrimEndMilliseconds,
    int FadeInMilliseconds,
    int FadeOutMilliseconds);

public sealed record CategoryDeleteResult(
    IReadOnlyList<SoundLibraryEntry> Sounds,
    IReadOnlyList<SoundCategory> Categories,
    int UncategorizedSoundCount);

public sealed record SoundImportResult(
    IReadOnlyList<SoundLibraryEntry> Imported,
    IReadOnlyList<DuplicateSoundImport> Duplicates,
    IReadOnlyList<FileImportFailure> InvalidFiles,
    IReadOnlyList<FileImportFailure> Errors)
{
    public string ToSummary()
    {
        return $"Imported: {Imported.Count} | "
            + $"Duplicates skipped: {Duplicates.Count} | "
            + $"Invalid files skipped: {InvalidFiles.Count} | "
            + $"Errors: {Errors.Count}";
    }
}

public sealed record DuplicateSoundImport(
    string SourceFileName,
    string ExistingDisplayName);

public sealed record FileImportFailure(
    string SourceFileName,
    string Reason);
