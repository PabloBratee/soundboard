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

/// <summary>
/// One sound's category membership at a single point in time.
/// </summary>
public sealed record SoundCategoryAssignment(
    Guid SoundId,
    Guid? CategoryId);

/// <summary>
/// Everything needed to put a completed category move back exactly the way
/// it was: the previous category of each moved sound plus the complete
/// library order that existed before the move.
/// </summary>
public sealed record SoundCategoryMoveUndo(
    IReadOnlyList<SoundCategoryAssignment> Assignments,
    IReadOnlyList<Guid> PreviousOrder)
{
    public bool CanUndo => Assignments.Count > 0;
}

public sealed record SoundCategoryMoveResult(
    IReadOnlyList<SoundLibraryEntry> Sounds,
    IReadOnlyList<Guid> MovedSoundIds,
    Guid? CategoryId,
    SoundCategoryMoveUndo Undo)
{
    public int MovedCount => MovedSoundIds.Count;
}

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
