using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Soundboard.Audio;

namespace Soundboard.App.Storage;

public sealed class SoundLibraryStore : IAsyncDisposable
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly List<SoundLibraryEntry> entries = [];
    private bool loaded;
    private bool disposed;

    public SoundLibraryStore(string? rootPath = null)
    {
        RootPath = rootPath
            ?? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Soundboard");
        SoundsPath = Path.Combine(RootPath, "Sounds");
        LibraryFilePath = Path.Combine(RootPath, "library.json");
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
            var loadedEntries = await ReadLibraryAsync(
                warnings,
                cancellationToken);

            entries.Clear();
            entries.AddRange(loadedEntries);
            loaded = true;

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

            return new SoundLibraryLoadResult(availableEntries, warnings);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<SoundImportResult> ImportAsync(
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();
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
                    await SaveEntriesCoreAsync(
                        updatedEntries,
                        cancellationToken);
                    entries.AddRange(imported);
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

    public async Task<SoundLibraryEntry> RenameAsync(
        Guid soundId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var trimmedName = displayName.Trim();
        if (trimmedName.Length == 0)
        {
            throw new ArgumentException(
                "A sound name cannot be empty.",
                nameof(displayName));
        }

        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();

            var index = entries.FindIndex(sound => sound.Id == soundId);
            if (index < 0)
            {
                throw new KeyNotFoundException(
                    "The sound no longer exists in the library.");
            }

            var renamed = entries[index] with
            {
                DisplayName = trimmedName
            };
            var updatedEntries = entries.ToList();
            updatedEntries[index] = renamed;
            await SaveEntriesCoreAsync(updatedEntries, cancellationToken);
            entries[index] = renamed;
            return renamed;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task RemoveAsync(
        Guid soundId,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureLoaded();

            var index = entries.FindIndex(sound => sound.Id == soundId);
            if (index < 0)
            {
                throw new KeyNotFoundException(
                    "The sound no longer exists in the library.");
            }

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

            try
            {
                await SaveEntriesCoreAsync(
                    updatedEntries,
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
                entries.RemoveAt(index);
            }
            catch (Exception deleteException)
                when (deleteException is IOException
                    or UnauthorizedAccessException)
            {
                try
                {
                    File.Move(stagedDeletePath, managedPath);
                    await SaveEntriesCoreAsync(entries, cancellationToken);
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
        }
        finally
        {
            operationGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            operationGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<List<SoundLibraryEntry>> ReadLibraryAsync(
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(LibraryFilePath))
        {
            return [];
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

            if (!document.RootElement.TryGetProperty(
                    "sounds",
                    out var soundsElement)
                || soundsElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException(
                    "The root object does not contain a sounds array.");
            }

            var result = new List<SoundLibraryEntry>();
            var ids = new HashSet<Guid>();
            foreach (var element in soundsElement.EnumerateArray())
            {
                try
                {
                    var entry = element.Deserialize<SoundLibraryEntry>(
                        JsonOptions)
                        ?? throw new JsonException("The entry was null.");
                    ValidateEntry(entry, ids);
                    result.Add(entry);
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
        catch (JsonException exception)
        {
            var timestamp = DateTime.UtcNow.ToString(
                "yyyyMMdd-HHmmssfff",
                System.Globalization.CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(
                RootPath,
                $"library.malformed-{timestamp}.json");

            try
            {
                File.Move(LibraryFilePath, backupPath);
                warnings.Add(
                    "The malformed library file was preserved as "
                    + $"{backupPath}. An empty library was created.");
                await SaveEntriesCoreAsync([], cancellationToken);
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
            return [];
        }
    }

    private async Task ImportOneAsync(
        string sourcePath,
        ICollection<SoundLibraryEntry> imported,
        ICollection<DuplicateSoundImport> duplicates,
        ICollection<FileImportFailure> invalidFiles,
        ICollection<FileImportFailure> errors,
        CancellationToken cancellationToken)
    {
        var sourceFileName = Path.GetFileName(sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".wav" and not ".mp3")
        {
            invalidFiles.Add(
                new FileImportFailure(
                    sourceFileName,
                    "Only WAV and MP3 files are supported."));
            return;
        }

        AudioFileDetails details;
        try
        {
            details = await Task.Run(
                () => AudioFileInspector.Inspect(sourcePath),
                cancellationToken);
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or NotSupportedException)
        {
            invalidFiles.Add(
                new FileImportFailure(sourceFileName, exception.Message));
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

        imported.Add(
            new SoundLibraryEntry(
                id,
                Path.GetFileNameWithoutExtension(sourceFileName),
                managedFileName,
                sourceFileName,
                extension[1..].ToUpperInvariant(),
                details.Duration,
                DateTimeOffset.UtcNow,
                entries.Count + imported.Count,
                contentHash));
    }

    private async Task SaveEntriesCoreAsync(
        IReadOnlyCollection<SoundLibraryEntry> sounds,
        CancellationToken cancellationToken)
    {
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
                        SchemaVersion,
                        sounds.ToArray()),
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
            || entry.FileType is not ("WAV" or "MP3")
            || entry.Duration <= TimeSpan.Zero
            || string.IsNullOrWhiteSpace(entry.ContentHash))
        {
            throw new ArgumentException(
                $"Sound {entry.Id} has invalid metadata.");
        }
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
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed record SoundLibraryDocument(
        int SchemaVersion,
        IReadOnlyList<SoundLibraryEntry> Sounds);
}
