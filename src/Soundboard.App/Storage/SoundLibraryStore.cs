using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soundboard.App.Hotkeys;
using Soundboard.Audio;

namespace Soundboard.App.Storage;

public sealed class SoundLibraryStore : IAsyncDisposable
{
    public const int CurrentSchemaVersion = 6;
    public const int MaximumCategoryNameLength = 60;
    public const int MaximumSoundNameLength = 120;
    public const long MaximumImportFileBytes = 1024L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly List<SoundLibraryEntry> entries = [];
    private readonly List<SoundCategory> categories = [];
    private readonly Func<CancellationToken, Task>? beforeSaveAsync;
    private readonly WaveformCacheService waveformCacheService;
    private readonly LoudnessAnalysisService loudnessAnalysisService;
    private bool loaded;
    private bool futureSchemaLoaded;
    private bool disposed;

    public SoundLibraryStore(
        string? rootPath = null,
        Func<CancellationToken, Task>? beforeSaveAsync = null)
    {
        RootPath = rootPath
            ?? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Soundboard");
        SoundsPath = Path.Combine(RootPath, "Sounds");
        LibraryFilePath = Path.Combine(RootPath, "library.json");
        waveformCacheService = new WaveformCacheService(RootPath);
        loudnessAnalysisService = new LoudnessAnalysisService(RootPath);
        this.beforeSaveAsync = beforeSaveAsync;
    }

    public string RootPath { get; }

    public string SoundsPath { get; }

    public string LibraryFilePath { get; }

    public string GetManagedFilePath(SoundLibraryEntry sound)
    {
        ArgumentNullException.ThrowIfNull(sound);
        return GetManagedFilePath(sound.ManagedFileName);
    }

    public async Task<SoundLibraryLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            Directory.CreateDirectory(SoundsPath);

            var warnings = new List<string>();
            var readResult = await ReadLibraryAsync(
                warnings,
                cancellationToken);

            entries.Clear();
            entries.AddRange(readResult.Sounds);
            categories.Clear();
            categories.AddRange(readResult.Categories);
            futureSchemaLoaded = readResult.FutureSchemaLoaded;
            loaded = true;

            if (readResult.NeedsSave)
            {
                try
                {
                    await SaveDocumentCoreAsync(
                        entries,
                        categories,
                        cancellationToken);
                }
                catch (Exception exception)
                    when (exception is IOException
                        or UnauthorizedAccessException)
                {
                    warnings.Add(
                        "The library was migrated in memory, but schema "
                        + $"version {CurrentSchemaVersion} could not be "
                        + $"saved: {exception.Message}");
                }
            }

            var availableEntries = new List<SoundLibraryEntry>();
            foreach (var entry in entries.OrderBy(sound => sound.SortOrder))
            {
                var managedPath = GetManagedFilePath(entry.ManagedFileName);
                try
                {
                    await using var stream = new FileStream(
                        managedPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 1,
                        FileOptions.Asynchronous);
                    availableEntries.Add(entry);
                }
                catch (Exception exception)
                    when (exception is IOException
                        or UnauthorizedAccessException)
                {
                    warnings.Add(
                        $"Skipped \"{entry.DisplayName}\" because its "
                        + $"managed file \"{entry.ManagedFileName}\" is "
                        + $"missing or unreadable: {exception.Message} "
                        + "Restore the file from backup or repair/remove its "
                        + $"record in {LibraryFilePath}.");
                }
            }

            warnings.AddRange(
                waveformCacheService.CleanupOrphans(
                    entries.Select(entry => entry.ContentHash)));
            warnings.AddRange(
                loudnessAnalysisService.CleanupOrphans(
                    entries.Select(entry => entry.ContentHash)));

            return new SoundLibraryLoadResult(
                availableEntries,
                categories.ToArray(),
                warnings);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SoundImportResult> ImportAsync(
        IEnumerable<string> sourcePaths,
        Guid? categoryId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();
            ValidateCategoryExists(categoryId);
            Directory.CreateDirectory(SoundsPath);

            var imported = new List<SoundLibraryEntry>();
            var duplicates = new List<DuplicateSoundImport>();
            var invalidFiles = new List<FileImportFailure>();
            var errors = new List<FileImportFailure>();

            foreach (var sourcePath in sourcePaths.Distinct(
                StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ImportOneAsync(
                    sourcePath,
                    categoryId,
                    imported,
                    duplicates,
                    invalidFiles,
                    errors,
                    cancellationToken);
            }

            if (imported.Count > 0)
            {
                try
                {
                    var updatedEntries = entries.Concat(imported).ToList();
                    NormalizeSoundOrder(updatedEntries, warnings: null);
                    await SaveDocumentCoreAsync(
                        updatedEntries,
                        categories,
                        cancellationToken);
                    entries.Clear();
                    entries.AddRange(updatedEntries);
                }
                catch (Exception exception)
                {
                    foreach (var sound in imported)
                    {
                        var rollbackWarning = TryDeleteFileWithWarning(
                            GetManagedFilePath(sound));
                        errors.Add(
                            new FileImportFailure(
                                sound.OriginalFileName,
                                "The managed copy was rolled back because "
                                + $"metadata could not be saved: "
                                + exception.Message
                                + rollbackWarning));
                    }

                    imported.Clear();
                }
            }

            return new SoundImportResult(
                imported,
                duplicates,
                invalidFiles,
                errors);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SoundLibraryEntry> UpdateSoundAsync(
        Guid soundId,
        SoundMetadataUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var displayName = NormalizeSoundName(update.DisplayName);
        if (!Enum.IsDefined(update.TileAccent))
        {
            throw new ArgumentException(
                "Select a supported tile accent.",
                nameof(update));
        }

        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();
            ValidateCategoryExists(update.CategoryId);

            var index = FindSoundIndex(soundId);
            var updated = entries[index] with
            {
                DisplayName = displayName,
                CategoryId = update.CategoryId,
                IsFavorite = update.IsFavorite,
                TileAccent = update.TileAccent
            };
            var updatedEntries = entries.ToList();
            updatedEntries[index] = updated;
            await SaveDocumentCoreAsync(
                updatedEntries,
                categories,
                cancellationToken);
            entries[index] = updated;
            return updated;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SoundLibraryEntry> RenameAsync(
        Guid soundId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();

            var index = FindSoundIndex(soundId);
            var renamed = entries[index] with
            {
                DisplayName = NormalizeSoundName(displayName)
            };
            var updatedEntries = entries.ToList();
            updatedEntries[index] = renamed;
            await SaveDocumentCoreAsync(
                updatedEntries,
                categories,
                cancellationToken);
            entries[index] = renamed;
            return renamed;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SoundLibraryEntry> UpdateClipSettingsAsync(
        Guid soundId,
        SoundClipMetadataUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();
            var index = FindSoundIndex(soundId);
            var current = entries[index];
            _ = AudioClipSettings.Create(
                current.Duration,
                update.TrimStartMilliseconds,
                update.TrimEndMilliseconds,
                update.FadeInMilliseconds,
                update.FadeOutMilliseconds);
            var updated = current with
            {
                TrimStartMilliseconds = update.TrimStartMilliseconds,
                TrimEndMilliseconds = update.TrimEndMilliseconds,
                FadeInMilliseconds = update.FadeInMilliseconds,
                FadeOutMilliseconds = update.FadeOutMilliseconds,
                NormalizeLoudness = update.NormalizeLoudness
            };
            var updatedEntries = entries.ToList();
            updatedEntries[index] = updated;
            await SaveDocumentCoreAsync(
                updatedEntries,
                categories,
                cancellationToken);
            entries[index] = updated;
            return updated;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SoundLibraryEntry> UpdateHotkeyAsync(
        Guid soundId,
        HotkeyGesture? hotkey,
        CancellationToken cancellationToken = default)
    {
        HotkeyGesture? normalizedHotkey = null;
        if (!HotkeyGesture.TryNormalize(
                hotkey,
                out normalizedHotkey,
                out var validationError))
        {
            throw new ArgumentException(
                validationError,
                nameof(hotkey));
        }

        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();

            var index = FindSoundIndex(soundId);
            if (normalizedHotkey is not null
                && entries.Any(
                    sound =>
                        sound.Id != soundId
                        && sound.Hotkey == normalizedHotkey))
            {
                throw new InvalidOperationException(
                    $"{normalizedHotkey.DisplayText} is already assigned "
                    + "to another sound.");
            }

            var updated = entries[index] with
            {
                Hotkey = normalizedHotkey
            };
            var updatedEntries = entries.ToList();
            updatedEntries[index] = updated;
            await SaveDocumentCoreAsync(
                updatedEntries,
                categories,
                cancellationToken);
            entries[index] = updated;
            return updated;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SoundCategory> CreateCategoryAsync(
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeCategoryName(displayName);

        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();
            EnsureUniqueCategoryName(normalizedName);

            var category = new SoundCategory(
                Guid.NewGuid(),
                normalizedName,
                categories.Count,
                DateTimeOffset.UtcNow);
            var updatedCategories = categories.Append(category).ToList();
            await SaveDocumentCoreAsync(
                entries,
                updatedCategories,
                cancellationToken);
            categories.Add(category);
            return category;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SoundCategory> RenameCategoryAsync(
        Guid categoryId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeCategoryName(displayName);

        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();
            var index = FindCategoryIndex(categoryId);
            EnsureUniqueCategoryName(normalizedName, categoryId);

            var renamed = categories[index] with
            {
                DisplayName = normalizedName
            };
            var updatedCategories = categories.ToList();
            updatedCategories[index] = renamed;
            await SaveDocumentCoreAsync(
                entries,
                updatedCategories,
                cancellationToken);
            categories[index] = renamed;
            return renamed;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<CategoryDeleteResult> DeleteCategoryAsync(
        Guid categoryId,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();
            _ = FindCategoryIndex(categoryId);

            var affectedCount = entries.Count(
                sound => sound.CategoryId == categoryId);
            var updatedEntries = entries
                .Select(
                    sound => sound.CategoryId == categoryId
                        ? sound with { CategoryId = null }
                        : sound)
                .ToList();
            var updatedCategories = categories
                .Where(category => category.Id != categoryId)
                .ToList();
            NormalizeCategoryOrder(updatedCategories, warnings: null);

            await SaveDocumentCoreAsync(
                updatedEntries,
                updatedCategories,
                cancellationToken);
            entries.Clear();
            entries.AddRange(updatedEntries);
            categories.Clear();
            categories.AddRange(updatedCategories);

            return new CategoryDeleteResult(
                entries.ToArray(),
                categories.ToArray(),
                affectedCount);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<SoundCategory>> ReorderCategoriesAsync(
        IReadOnlyList<Guid> orderedCategoryIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedCategoryIds);

        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();
            if (orderedCategoryIds.Count != categories.Count
                || orderedCategoryIds.Distinct().Count()
                    != orderedCategoryIds.Count
                || orderedCategoryIds.Any(
                    id => categories.All(category => category.Id != id)))
            {
                throw new ArgumentException(
                    "The category order must contain every user category "
                    + "exactly once.",
                    nameof(orderedCategoryIds));
            }

            var byId = categories.ToDictionary(category => category.Id);
            var reordered = orderedCategoryIds
                .Select((id, index) => byId[id] with { SortOrder = index })
                .ToList();
            await SaveDocumentCoreAsync(
                entries,
                reordered,
                cancellationToken);
            categories.Clear();
            categories.AddRange(reordered);
            return categories.ToArray();
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<SoundLibraryEntry>> ReorderSoundsAsync(
        IReadOnlyList<Guid> orderedSoundIds,
        Guid? categoryId,
        bool allSounds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedSoundIds);

        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();
            if (!allSounds)
            {
                ValidateCategoryExists(categoryId);
            }

            var distinctIds = orderedSoundIds.Distinct().ToArray();
            if (distinctIds.Length != orderedSoundIds.Count)
            {
                throw new ArgumentException(
                    "A sound may appear only once in a reorder operation.",
                    nameof(orderedSoundIds));
            }

            var orderedEntries = entries
                .OrderBy(sound => sound.SortOrder)
                .ToList();
            var byId = orderedEntries.ToDictionary(sound => sound.Id);
            foreach (var id in orderedSoundIds)
            {
                if (!byId.TryGetValue(id, out var sound))
                {
                    throw new KeyNotFoundException(
                        $"Sound {id} no longer exists in the library.");
                }

                if (!allSounds && sound.CategoryId != categoryId)
                {
                    throw new ArgumentException(
                        "Every reordered sound must belong to the selected "
                        + "library view.",
                        nameof(orderedSoundIds));
                }
            }

            var movingIds = orderedSoundIds.ToHashSet();
            var movingSlots = Enumerable.Range(0, orderedEntries.Count)
                .Where(index => movingIds.Contains(orderedEntries[index].Id))
                .ToArray();
            for (var index = 0; index < movingSlots.Length; index++)
            {
                orderedEntries[movingSlots[index]] =
                    byId[orderedSoundIds[index]];
            }

            for (var index = 0; index < orderedEntries.Count; index++)
            {
                orderedEntries[index] = orderedEntries[index] with
                {
                    SortOrder = index
                };
            }

            await SaveDocumentCoreAsync(
                orderedEntries,
                categories,
                cancellationToken);
            entries.Clear();
            entries.AddRange(orderedEntries);
            return entries.ToArray();
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> RemoveAsync(
        Guid soundId,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();

            var index = FindSoundIndex(soundId);
            var sound = entries[index];
            var managedPath = GetManagedFilePath(sound);
            var stagedDeletePath = managedPath
                + $".deleting-{Guid.NewGuid():N}";

            try
            {
                File.Move(managedPath, stagedDeletePath);
            }
            catch (Exception exception)
                when (exception is IOException
                    or UnauthorizedAccessException)
            {
                throw new IOException(
                    "The managed audio file could not be removed. "
                    + "Library metadata was left unchanged.",
                    exception);
            }

            var updatedEntries = entries
                .Where(entry => entry.Id != soundId)
                .ToList();
            NormalizeSoundOrder(updatedEntries, warnings: null);

            try
            {
                await SaveDocumentCoreAsync(
                    updatedEntries,
                    categories,
                    cancellationToken);
            }
            catch (Exception saveException)
            {
                try
                {
                    File.Move(stagedDeletePath, managedPath);
                }
                catch (Exception restoreException)
                    when (restoreException is IOException
                        or UnauthorizedAccessException)
                {
                    throw new IOException(
                        "Metadata removal failed and the managed file "
                        + "could not be moved back into place. Inspect "
                        + $"{RootPath} before making further changes.",
                        new AggregateException(
                            saveException,
                            restoreException));
                }

                throw;
            }

            try
            {
                File.Delete(stagedDeletePath);
                entries.Clear();
                entries.AddRange(updatedEntries);
            }
            catch (Exception deleteException)
                when (deleteException is IOException
                    or UnauthorizedAccessException)
            {
                try
                {
                    File.Move(stagedDeletePath, managedPath);
                    await SaveDocumentCoreAsync(
                        entries,
                        categories,
                        cancellationToken);
                }
                catch (Exception rollbackException)
                {
                    throw new IOException(
                        "Managed-file deletion failed and automatic "
                        + "metadata rollback also failed. Inspect "
                        + $"{RootPath} before making further changes.",
                        new AggregateException(
                            deleteException,
                            rollbackException));
                }

                throw new IOException(
                    "The managed audio file could not be deleted. The "
                    + "sound was restored to the library.",
                    deleteException);
            }

            var cacheWarnings = waveformCacheService.DeleteForContentHash(
                sound.ContentHash).ToList();
            cacheWarnings.AddRange(
                loudnessAnalysisService.DeleteForContentHash(
                    sound.ContentHash));
            return cacheWarnings;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            operationGate.Dispose();
            await loudnessAnalysisService.DisposeAsync();
        }
    }

    private async Task<LibraryReadResult> ReadLibraryAsync(
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(LibraryFilePath))
        {
            return new LibraryReadResult(
                [],
                [],
                NeedsSave: false,
                FutureSchemaLoaded: false);
        }

        try
        {
            await using var stream = new FileStream(
                LibraryFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);

            var schemaVersion = 1;
            if (document.RootElement.TryGetProperty(
                    "schemaVersion",
                    out var schemaElement)
                && schemaElement.TryGetInt32(out var parsedSchema))
            {
                schemaVersion = parsedSchema;
            }

            var needsSave = schemaVersion < CurrentSchemaVersion;
            if (schemaVersion < CurrentSchemaVersion)
            {
                warnings.Add(
                    $"Migrated sound library schema version {schemaVersion} "
                    + $"to version {CurrentSchemaVersion}.");
            }
            else if (schemaVersion > CurrentSchemaVersion)
            {
                warnings.Add(
                    $"Library schema version {schemaVersion} is newer than "
                    + $"supported version {CurrentSchemaVersion}. Known "
                    + "metadata was loaded conservatively.");
            }

            var loadedCategories = ReadCategories(
                document.RootElement,
                warnings,
                ref needsSave);

            if (!document.RootElement.TryGetProperty(
                    "sounds",
                    out var soundsElement)
                || soundsElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException(
                    "The root object does not contain a sounds array.");
            }

            var loadedEntries = ReadSounds(
                soundsElement,
                loadedCategories,
                warnings,
                ref needsSave);

            if (NormalizeCategoryOrder(loadedCategories, warnings))
            {
                needsSave = true;
            }

            if (NormalizeSoundOrder(loadedEntries, warnings))
            {
                needsSave = true;
            }

            if (schemaVersion > CurrentSchemaVersion)
            {
                needsSave = false;
            }

            return new LibraryReadResult(
                loadedEntries,
                loadedCategories,
                needsSave,
                schemaVersion > CurrentSchemaVersion);
        }
        catch (JsonException exception)
        {
            var timestamp = DateTime.UtcNow.ToString(
                "yyyyMMdd-HHmmssfff",
                CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(
                RootPath,
                $"library.malformed-{timestamp}.json");

            try
            {
                File.Move(LibraryFilePath, backupPath);
                warnings.Add(
                    "The malformed library file was preserved as "
                    + $"{backupPath}. An empty library was created.");
                await SaveDocumentCoreAsync([], [], cancellationToken);
            }
            catch (Exception backupException)
                when (backupException is IOException
                    or UnauthorizedAccessException)
            {
                warnings.Add(
                    "The library metadata is malformed and could not be "
                    + $"backed up or replaced: {backupException.Message}");
            }

            warnings.Add($"Malformed library details: {exception.Message}");
            return new LibraryReadResult(
                [],
                [],
                NeedsSave: false,
                FutureSchemaLoaded: false);
        }
    }

    private static List<SoundCategory> ReadCategories(
        JsonElement root,
        ICollection<string> warnings,
        ref bool needsSave)
    {
        var result = new List<SoundCategory>();
        if (!root.TryGetProperty("categories", out var categoriesElement))
        {
            return result;
        }

        if (categoriesElement.ValueKind != JsonValueKind.Array)
        {
            warnings.Add(
                "Invalid category metadata was ignored; sounds remain "
                + "available as Uncategorized.");
            needsSave = true;
            return result;
        }

        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in categoriesElement.EnumerateArray())
        {
            try
            {
                var category = element.Deserialize<SoundCategory>(JsonOptions)
                    ?? throw new JsonException("The category was null.");
                var normalizedName =
                    NormalizeCategoryName(category.DisplayName);
                if (category.Id == Guid.Empty || !ids.Add(category.Id))
                {
                    throw new ArgumentException(
                        "The category ID is empty or duplicated.");
                }

                if (!names.Add(normalizedName))
                {
                    throw new ArgumentException(
                        $"Category name \"{normalizedName}\" is duplicated.");
                }

                if (category.CreatedAtUtc == default)
                {
                    throw new ArgumentException(
                        $"Category {category.Id} has no creation date.");
                }

                var createdAtUtc = category.CreatedAtUtc.ToUniversalTime();
                result.Add(
                    category with
                    {
                        DisplayName = normalizedName,
                        CreatedAtUtc = createdAtUtc
                    });
                if (!string.Equals(
                        category.DisplayName,
                        normalizedName,
                        StringComparison.Ordinal)
                    || category.CreatedAtUtc.Offset != TimeSpan.Zero)
                {
                    needsSave = true;
                }
            }
            catch (Exception exception)
                when (exception is JsonException
                    or ArgumentException)
            {
                warnings.Add(
                    "Ignored invalid category metadata: "
                    + exception.Message
                    + " Sounds assigned to it remain available as "
                    + "Uncategorized.");
                needsSave = true;
            }
        }

        return result;
    }

    private static List<SoundLibraryEntry> ReadSounds(
        JsonElement soundsElement,
        IReadOnlyCollection<SoundCategory> loadedCategories,
        ICollection<string> warnings,
        ref bool needsSave)
    {
        var result = new List<SoundLibraryEntry>();
        var ids = new HashSet<Guid>();
        var hotkeys = new Dictionary<HotkeyGesture, Guid>();
        var categoryIds = loadedCategories
            .Select(category => category.Id)
            .ToHashSet();

        foreach (var element in soundsElement.EnumerateArray())
        {
            try
            {
                var serialized =
                    element.Deserialize<SoundLibraryEntryDocument>(
                        JsonOptions)
                    ?? throw new JsonException("The entry was null.");
                var hotkey = ReadHotkey(
                    serialized.Id,
                    serialized.Hotkey,
                    hotkeys,
                    warnings,
                    ref needsSave);
                var categoryId = ReadCategoryId(
                    serialized.Id,
                    serialized.CategoryId,
                    categoryIds,
                    warnings,
                    ref needsSave);
                var isFavorite = ReadFavorite(
                    serialized.Id,
                    serialized.IsFavorite,
                    warnings,
                    ref needsSave);
                var tileAccent = ReadTileAccent(
                    serialized.Id,
                    serialized.TileAccent,
                    warnings,
                    ref needsSave);
                var detectedFormat = ReadDetectedFormat(
                    serialized.Id,
                    serialized.FileType,
                    serialized.OriginalFileName,
                    serialized.ManagedFileName,
                    serialized.Container,
                    serialized.Codec,
                    serialized.OriginalExtension,
                    warnings,
                    ref needsSave);
                var clipSettings = ReadClipSettings(
                    serialized,
                    warnings,
                    ref needsSave);
                var normalizeLoudness = ReadNormalizationEnabled(
                    serialized.Id,
                    serialized.NormalizeLoudness,
                    warnings,
                    ref needsSave);

                var entry = new SoundLibraryEntry(
                    serialized.Id,
                    serialized.DisplayName,
                    serialized.ManagedFileName,
                    serialized.OriginalFileName,
                    detectedFormat.DisplayLabel,
                    serialized.Duration,
                    serialized.ImportedAtUtc,
                    serialized.SortOrder,
                    serialized.ContentHash,
                    hotkey,
                    categoryId,
                    isFavorite,
                    tileAccent,
                    detectedFormat.Container,
                    detectedFormat.Codec,
                    detectedFormat.OriginalExtension,
                    clipSettings.TrimStartMilliseconds,
                    clipSettings.TrimEndMilliseconds,
                    clipSettings.FadeInMilliseconds,
                    clipSettings.FadeOutMilliseconds,
                    normalizeLoudness);
                ValidateEntry(entry, ids);
                result.Add(entry);
                if (hotkey is not null)
                {
                    hotkeys.Add(hotkey, entry.Id);
                }
            }
            catch (Exception exception)
                when (exception is JsonException
                    or ArgumentException)
            {
                warnings.Add(
                    "Skipped an invalid library entry: "
                    + exception.Message);
            }
        }

        return result;
    }

    private static HotkeyGesture? ReadHotkey(
        Guid soundId,
        JsonElement? element,
        IReadOnlyDictionary<HotkeyGesture, Guid> hotkeys,
        ICollection<string> warnings,
        ref bool needsSave)
    {
        if (element is not { } hotkeyElement
            || hotkeyElement.ValueKind
                is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        try
        {
            var candidate = hotkeyElement.Deserialize<HotkeyGesture>(
                JsonOptions);
            if (!HotkeyGesture.TryNormalize(
                    candidate,
                    out var hotkey,
                    out var hotkeyError))
            {
                warnings.Add(
                    $"Sound {soundId} was loaded without its invalid "
                    + $"hotkey: {hotkeyError}");
                needsSave = true;
                return null;
            }

            if (hotkey is not null
                && hotkeys.TryGetValue(hotkey, out var conflictingSoundId))
            {
                warnings.Add(
                    $"Sound {soundId} was loaded without hotkey "
                    + $"{hotkey.DisplayText} because it duplicates the "
                    + $"assignment for sound {conflictingSoundId}.");
                needsSave = true;
                return null;
            }

            return hotkey;
        }
        catch (JsonException exception)
        {
            warnings.Add(
                $"Sound {soundId} was loaded without its invalid hotkey: "
                + exception.Message);
            needsSave = true;
            return null;
        }
    }

    private static SoundClipMetadataUpdate ReadClipSettings(
        SoundLibraryEntryDocument serialized,
        ICollection<string> warnings,
        ref bool needsSave)
    {
        try
        {
            var trimStart = ReadOptionalNonNegativeInt32(
                serialized.TrimStartMilliseconds,
                defaultValue: 0,
                "trim start");
            var trimEnd = ReadOptionalNullableNonNegativeInt32(
                serialized.TrimEndMilliseconds,
                "trim end");
            var fadeIn = ReadOptionalNonNegativeInt32(
                serialized.FadeInMilliseconds,
                defaultValue: 0,
                "fade in");
            var fadeOut = ReadOptionalNonNegativeInt32(
                serialized.FadeOutMilliseconds,
                defaultValue: 0,
                "fade out");

            _ = trimStart == 0
                && trimEnd is null
                && fadeIn == 0
                && fadeOut == 0
                    ? AudioClipSettings.FullDuration(serialized.Duration)
                    : AudioClipSettings.Create(
                        serialized.Duration,
                        trimStart,
                        trimEnd,
                        fadeIn,
                        fadeOut);
            return new SoundClipMetadataUpdate(
                trimStart,
                trimEnd,
                fadeIn,
                fadeOut);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidOperationException
                or OverflowException)
        {
            warnings.Add(
                $"Sound {serialized.Id} had invalid clip edit metadata and "
                + "was reset to its full original duration with no fades: "
                + exception.Message);
            needsSave = true;
            return new SoundClipMetadataUpdate(0, null, 0, 0);
        }
    }

    private static int ReadOptionalNonNegativeInt32(
        JsonElement? element,
        int defaultValue,
        string fieldName)
    {
        if (element is not { } value
            || value.ValueKind is JsonValueKind.Null
                or JsonValueKind.Undefined)
        {
            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var parsed)
            || parsed < 0)
        {
            throw new ArgumentException(
                $"The persisted {fieldName} value is not a safe "
                + "non-negative integer.");
        }

        return parsed;
    }

    private static bool ReadNormalizationEnabled(
        Guid soundId,
        JsonElement? element,
        ICollection<string> warnings,
        ref bool needsSave)
    {
        if (element is not { } value
            || value.ValueKind
                is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        warnings.Add(
            $"Sound {soundId} had an invalid loudness-normalization value "
            + "and was loaded with normalization disabled.");
        needsSave = true;
        return false;
    }

    private static int? ReadOptionalNullableNonNegativeInt32(
        JsonElement? element,
        string fieldName)
    {
        if (element is not { } value
            || value.ValueKind is JsonValueKind.Null
                or JsonValueKind.Undefined)
        {
            return null;
        }

        return ReadOptionalNonNegativeInt32(value, 0, fieldName);
    }

    private static Guid? ReadCategoryId(
        Guid soundId,
        JsonElement? element,
        IReadOnlySet<Guid> categoryIds,
        ICollection<string> warnings,
        ref bool needsSave)
    {
        if (element is not { } categoryElement
            || categoryElement.ValueKind
                is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (categoryElement.ValueKind == JsonValueKind.String
            && Guid.TryParse(categoryElement.GetString(), out var categoryId)
            && categoryIds.Contains(categoryId))
        {
            return categoryId;
        }

        warnings.Add(
            $"Sound {soundId} had an invalid or missing category reference "
            + "and was loaded as Uncategorized.");
        needsSave = true;
        return null;
    }

    private static bool ReadFavorite(
        Guid soundId,
        JsonElement? element,
        ICollection<string> warnings,
        ref bool needsSave)
    {
        if (element is not { } favoriteElement
            || favoriteElement.ValueKind
                is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        if (favoriteElement.ValueKind is JsonValueKind.True)
        {
            return true;
        }

        if (favoriteElement.ValueKind is JsonValueKind.False)
        {
            return false;
        }

        warnings.Add(
            $"Sound {soundId} had an invalid favorite value and was "
            + "loaded as not favorite.");
        needsSave = true;
        return false;
    }

    private static SoundTileAccent ReadTileAccent(
        Guid soundId,
        JsonElement? element,
        ICollection<string> warnings,
        ref bool needsSave)
    {
        if (element is not { } accentElement
            || accentElement.ValueKind
                is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return SoundTileAccent.Default;
        }

        if (accentElement.ValueKind == JsonValueKind.String
            && Enum.TryParse<SoundTileAccent>(
                accentElement.GetString(),
                ignoreCase: true,
                out var stringAccent)
            && Enum.IsDefined(stringAccent))
        {
            return stringAccent;
        }

        if (accentElement.ValueKind == JsonValueKind.Number
            && accentElement.TryGetInt32(out var numericAccent)
            && Enum.IsDefined((SoundTileAccent)numericAccent))
        {
            return (SoundTileAccent)numericAccent;
        }

        warnings.Add(
            $"Sound {soundId} had an invalid tile accent and was loaded "
            + "with the Default accent.");
        needsSave = true;
        return SoundTileAccent.Default;
    }

    private async Task ImportOneAsync(
        string sourcePath,
        Guid? categoryId,
        ICollection<SoundLibraryEntry> imported,
        ICollection<DuplicateSoundImport> duplicates,
        ICollection<FileImportFailure> invalidFiles,
        ICollection<FileImportFailure> errors,
        CancellationToken cancellationToken)
    {
        var sourceFileName = Path.GetFileName(sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (
            ".wav" or ".mp3" or ".ogg" or ".opus"))
        {
            invalidFiles.Add(
                new FileImportFailure(
                    sourceFileName,
                    "Only WAV, MP3, Ogg Opus, and Ogg Vorbis audio files "
                    + "are supported."));
            return;
        }

        AudioFileDetails details;
        try
        {
            var sourceLength = new FileInfo(sourcePath).Length;
            if (sourceLength == 0)
            {
                throw new InvalidDataException(
                    "The audio file is empty.");
            }

            if (sourceLength > MaximumImportFileBytes)
            {
                throw new InvalidDataException(
                    "The audio file exceeds the 1 GiB import limit.");
            }

            details = await Task.Run(
                () => AudioFileInspector.Inspect(sourcePath),
                cancellationToken);
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                or NotSupportedException)
        {
            invalidFiles.Add(
                new FileImportFailure(sourceFileName, exception.Message));
            return;
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException)
        {
            errors.Add(
                new FileImportFailure(
                    sourceFileName,
                    "The file is missing, locked, or unreadable: "
                    + exception.Message));
            return;
        }

        string contentHash;
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(
                source,
                cancellationToken);
            contentHash = Convert.ToHexString(hash);
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException)
        {
            errors.Add(
                new FileImportFailure(sourceFileName, exception.Message));
            return;
        }

        var duplicate = entries
            .Concat(imported)
            .FirstOrDefault(
                sound => string.Equals(
                    sound.ContentHash,
                    contentHash,
                    StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            duplicates.Add(
                new DuplicateSoundImport(
                    sourceFileName,
                    duplicate.DisplayName));
            return;
        }

        var id = Guid.NewGuid();
        var managedFileName = $"{id:N}{extension}";
        var managedPath = GetManagedFilePath(managedFileName);
        var importingPath = managedPath + ".importing";

        try
        {
            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                importingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            File.Move(importingPath, managedPath);
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException)
        {
            TryDeleteFile(importingPath);
            TryDeleteFile(managedPath);
            errors.Add(
                new FileImportFailure(sourceFileName, exception.Message));
            return;
        }

        var importedDisplayName =
            Path.GetFileNameWithoutExtension(sourceFileName);
        if (importedDisplayName.Length > MaximumSoundNameLength)
        {
            importedDisplayName =
                importedDisplayName[..MaximumSoundNameLength];
        }

        imported.Add(
            new SoundLibraryEntry(
                id,
                importedDisplayName,
                managedFileName,
                sourceFileName,
                details.Format.DisplayLabel,
                details.Duration,
                DateTimeOffset.UtcNow,
                entries.Count + imported.Count,
                contentHash,
                Hotkey: null,
                CategoryId: categoryId,
                IsFavorite: false,
                TileAccent: SoundTileAccent.Default,
                Container: details.Format.Container,
                Codec: details.Format.Codec,
                OriginalExtension: details.Format.OriginalExtension));
    }

    private async Task SaveDocumentCoreAsync(
        IReadOnlyCollection<SoundLibraryEntry> sounds,
        IReadOnlyCollection<SoundCategory> soundCategories,
        CancellationToken cancellationToken)
    {
        if (beforeSaveAsync is not null)
        {
            await beforeSaveAsync(cancellationToken);
        }

        Directory.CreateDirectory(RootPath);
        var temporaryPath = LibraryFilePath
            + $".tmp-{Guid.NewGuid():N}";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new SoundLibraryDocument(
                        CurrentSchemaVersion,
                        soundCategories
                            .OrderBy(category => category.SortOrder)
                            .ToArray(),
                        sounds
                            .OrderBy(sound => sound.SortOrder)
                            .ToArray()),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(LibraryFilePath))
            {
                File.Replace(
                    temporaryPath,
                    LibraryFilePath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, LibraryFilePath);
            }
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private string GetManagedFilePath(string managedFileName)
    {
        var fileName = Path.GetFileName(managedFileName);
        if (!string.Equals(
                fileName,
                managedFileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Managed audio filenames may not contain directories.");
        }

        return Path.Combine(SoundsPath, fileName);
    }

    private static AudioFileFormat ReadDetectedFormat(
        Guid soundId,
        string? legacyFileType,
        string originalFileName,
        string managedFileName,
        JsonElement? containerElement,
        JsonElement? codecElement,
        JsonElement? originalExtensionElement,
        ICollection<string> warnings,
        ref bool needsSave)
    {
        var inferredExtension = Path.GetExtension(originalFileName)
            .ToLowerInvariant();
        if (inferredExtension is not (
            ".wav" or ".mp3" or ".ogg" or ".opus"))
        {
            inferredExtension = Path.GetExtension(managedFileName)
                .ToLowerInvariant();
        }

        AudioFileFormat inferred;
        if (legacyFileType?.Contains(
                "Opus",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            inferred = new AudioFileFormat(
                AudioContainerType.Ogg,
                AudioCodecType.Opus,
                inferredExtension is ".ogg" or ".opus"
                    ? inferredExtension
                    : ".ogg");
        }
        else if (legacyFileType?.Contains(
                     "Vorbis",
                     StringComparison.OrdinalIgnoreCase) == true)
        {
            inferred = new AudioFileFormat(
                AudioContainerType.Ogg,
                AudioCodecType.Vorbis,
                inferredExtension is ".ogg" or ".opus"
                    ? inferredExtension
                    : ".ogg");
        }
        else if (string.Equals(
                     legacyFileType,
                     "MP3",
                     StringComparison.OrdinalIgnoreCase)
                 || inferredExtension == ".mp3")
        {
            inferred = new AudioFileFormat(
                AudioContainerType.Mp3,
                AudioCodecType.MpegLayer3,
                ".mp3");
        }
        else
        {
            inferred = new AudioFileFormat(
                AudioContainerType.Wav,
                AudioCodecType.Pcm,
                ".wav");
        }

        if (!TryReadEnum(containerElement, out AudioContainerType container)
            || !TryReadEnum(codecElement, out AudioCodecType codec)
            || !TryReadExtension(
                originalExtensionElement,
                out var originalExtension)
            || !IsValidFormatCombination(container, codec)
            || !IsValidOriginalExtension(container, originalExtension))
        {
            if (containerElement is not null
                || codecElement is not null
                || originalExtensionElement is not null)
            {
                warnings.Add(
                    $"Sound {soundId} had invalid detected-format metadata "
                    + $"and was loaded as {inferred.DisplayLabel}.");
            }

            needsSave = true;
            return inferred;
        }

        return new AudioFileFormat(container, codec, originalExtension);
    }

    private static bool TryReadEnum<TEnum>(
        JsonElement? element,
        out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (element is not { } enumElement)
        {
            return false;
        }

        if (enumElement.ValueKind == JsonValueKind.String)
        {
            return Enum.TryParse(
                    enumElement.GetString(),
                    ignoreCase: true,
                    out value)
                && Enum.IsDefined(value);
        }

        if (enumElement.ValueKind == JsonValueKind.Number
            && enumElement.TryGetInt32(out var numeric))
        {
            value = (TEnum)Enum.ToObject(typeof(TEnum), numeric);
            return Enum.IsDefined(value);
        }

        return false;
    }

    private static bool TryReadExtension(
        JsonElement? element,
        out string extension)
    {
        extension = string.Empty;
        if (element is not { ValueKind: JsonValueKind.String } value)
        {
            return false;
        }

        extension = value.GetString()?.ToLowerInvariant() ?? string.Empty;
        return extension is ".wav" or ".mp3" or ".ogg" or ".opus";
    }

    private static bool IsValidFormatCombination(
        AudioContainerType container,
        AudioCodecType codec)
    {
        return (container, codec) is
            (AudioContainerType.Wav, AudioCodecType.Pcm)
            or (AudioContainerType.Mp3, AudioCodecType.MpegLayer3)
            or (AudioContainerType.Ogg, AudioCodecType.Opus)
            or (AudioContainerType.Ogg, AudioCodecType.Vorbis);
    }

    private static bool IsValidOriginalExtension(
        AudioContainerType container,
        string extension)
    {
        return (container, extension) is
            (AudioContainerType.Wav, ".wav")
            or (AudioContainerType.Mp3, ".mp3")
            or (AudioContainerType.Ogg, ".ogg")
            or (AudioContainerType.Ogg, ".opus");
    }

    private static bool NormalizeSoundOrder(
        List<SoundLibraryEntry> sounds,
        ICollection<string>? warnings)
    {
        var ordered = sounds.OrderBy(sound => sound.SortOrder).ToList();
        var changed = false;
        for (var index = 0; index < ordered.Count; index++)
        {
            if (ordered[index].SortOrder != index
                || !ReferenceEquals(ordered[index], sounds[index]))
            {
                changed = true;
            }

            ordered[index] = ordered[index] with { SortOrder = index };
        }

        if (changed)
        {
            sounds.Clear();
            sounds.AddRange(ordered);
            warnings?.Add(
                "Normalized sound sort order while preserving the existing "
                + "library sequence.");
        }

        return changed;
    }

    private static bool NormalizeCategoryOrder(
        List<SoundCategory> soundCategories,
        ICollection<string>? warnings)
    {
        var ordered = soundCategories
            .OrderBy(category => category.SortOrder)
            .ToList();
        var changed = false;
        for (var index = 0; index < ordered.Count; index++)
        {
            if (ordered[index].SortOrder != index
                || !ReferenceEquals(ordered[index], soundCategories[index]))
            {
                changed = true;
            }

            ordered[index] = ordered[index] with { SortOrder = index };
        }

        if (changed)
        {
            soundCategories.Clear();
            soundCategories.AddRange(ordered);
            warnings?.Add(
                "Normalized category sort order while preserving the "
                + "existing category sequence.");
        }

        return changed;
    }

    private static void ValidateEntry(
        SoundLibraryEntry entry,
        ISet<Guid> ids)
    {
        if (entry.Id == Guid.Empty || !ids.Add(entry.Id))
        {
            throw new ArgumentException(
                "The sound ID is empty or duplicated.");
        }

        if (string.IsNullOrWhiteSpace(entry.DisplayName)
            || string.IsNullOrWhiteSpace(entry.ManagedFileName)
            || Path.GetFileName(entry.ManagedFileName)
                != entry.ManagedFileName
            || string.IsNullOrWhiteSpace(entry.OriginalFileName)
            || !IsValidFormatCombination(entry.Container, entry.Codec)
            || !IsValidOriginalExtension(
                entry.Container,
                entry.OriginalExtension)
            || entry.Duration <= TimeSpan.Zero
            || string.IsNullOrWhiteSpace(entry.ContentHash))
        {
            throw new ArgumentException(
                $"Sound {entry.Id} has invalid metadata.");
        }
    }

    private static string NormalizeSoundName(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var normalizedName = displayName.Trim();
        if (normalizedName.Length == 0)
        {
            throw new ArgumentException(
                "A sound name cannot be empty.",
                nameof(displayName));
        }

        if (normalizedName.Length > MaximumSoundNameLength)
        {
            throw new ArgumentException(
                $"A sound name cannot exceed "
                + $"{MaximumSoundNameLength} characters.",
                nameof(displayName));
        }

        return normalizedName;
    }

    private static string NormalizeCategoryName(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var normalizedName = displayName.Trim();
        if (normalizedName.Length == 0)
        {
            throw new ArgumentException(
                "A category name cannot be empty.",
                nameof(displayName));
        }

        if (normalizedName.Length > MaximumCategoryNameLength)
        {
            throw new ArgumentException(
                $"A category name cannot exceed "
                + $"{MaximumCategoryNameLength} characters.",
                nameof(displayName));
        }

        return normalizedName;
    }

    private void EnsureUniqueCategoryName(
        string displayName,
        Guid? exceptCategoryId = null)
    {
        if (displayName.Equals(
                "All Sounds",
                StringComparison.OrdinalIgnoreCase)
            || displayName.Equals(
                "Favorites",
                StringComparison.OrdinalIgnoreCase)
            || displayName.Equals(
                "Uncategorized",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"\"{displayName}\" is a built-in library view name. "
                + "Choose another category name.");
        }

        if (categories.Any(
                category =>
                    category.Id != exceptCategoryId
                    && string.Equals(
                        category.DisplayName,
                        displayName,
                        StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"A category named \"{displayName}\" already exists.");
        }
    }

    private void ValidateCategoryExists(Guid? categoryId)
    {
        if (categoryId is not null
            && categories.All(category => category.Id != categoryId))
        {
            throw new KeyNotFoundException(
                "The selected category no longer exists.");
        }
    }

    private int FindSoundIndex(Guid soundId)
    {
        var index = entries.FindIndex(sound => sound.Id == soundId);
        if (index < 0)
        {
            throw new KeyNotFoundException(
                "The sound no longer exists in the library.");
        }

        return index;
    }

    private int FindCategoryIndex(Guid categoryId)
    {
        var index = categories.FindIndex(
            category => category.Id == categoryId);
        if (index < 0)
        {
            throw new KeyNotFoundException(
                "The category no longer exists.");
        }

        return index;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A later startup can safely ignore temporary import/save files.
        }
    }

    private static string TryDeleteFileWithWarning(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return string.Empty;
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException)
        {
            return " The failed managed copy could not be cleaned up: "
                + exception.Message;
        }
    }

    private void EnsureLoaded()
    {
        if (!loaded)
        {
            throw new InvalidOperationException(
                "Load the sound library before modifying it.");
        }

        if (futureSchemaLoaded)
        {
            throw new InvalidOperationException(
                "This library was written by a newer Soundboard schema. "
                + "It is available read-only so unknown metadata is not "
                + "silently downgraded.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record LibraryReadResult(
        List<SoundLibraryEntry> Sounds,
        List<SoundCategory> Categories,
        bool NeedsSave,
        bool FutureSchemaLoaded);

    private sealed record SoundLibraryDocument(
        int SchemaVersion,
        IReadOnlyList<SoundCategory> Categories,
        IReadOnlyList<SoundLibraryEntry> Sounds);

    private sealed record SoundLibraryEntryDocument(
        Guid Id,
        string DisplayName,
        string ManagedFileName,
        string OriginalFileName,
        string FileType,
        TimeSpan Duration,
        DateTimeOffset ImportedAtUtc,
        int SortOrder,
        string ContentHash,
        JsonElement? Hotkey,
        JsonElement? CategoryId,
        JsonElement? IsFavorite,
        JsonElement? TileAccent,
        JsonElement? Container,
        JsonElement? Codec,
        JsonElement? OriginalExtension,
        JsonElement? TrimStartMilliseconds,
        JsonElement? TrimEndMilliseconds,
        JsonElement? FadeInMilliseconds,
        JsonElement? FadeOutMilliseconds,
        JsonElement? NormalizeLoudness);
}
