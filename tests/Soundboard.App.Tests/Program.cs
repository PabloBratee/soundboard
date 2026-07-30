using System.Text.Json;
using Soundboard.App.Hotkeys;
using Soundboard.App.Presentation;
using Soundboard.App.Storage;

namespace Soundboard.App.Tests;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> Main()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"Soundboard-Milestone6-Tests-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(testRoot);
            await RunMigrationAndOrganizationTestsAsync(
                Path.Combine(testRoot, "organization"));
            await RunImportAndSearchTestsAsync(
                Path.Combine(testRoot, "import"));
            await RunInvalidMetadataFallbackTestAsync(
                Path.Combine(testRoot, "invalid-metadata"));
            await RunVersionOneMigrationTestAsync(
                Path.Combine(testRoot, "version-one"));
            Console.WriteLine("All Milestone 6 library tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            TryDeleteTestDirectory(testRoot);
        }
    }

    private static async Task RunMigrationAndOrganizationTestsAsync(
        string root)
    {
        var soundsPath = Path.Combine(root, "Sounds");
        Directory.CreateDirectory(soundsPath);
        var soundA = Guid.NewGuid();
        var soundB = Guid.NewGuid();
        var soundC = Guid.NewGuid();
        var soundD = Guid.NewGuid();
        var hotkey = new HotkeyGesture(
            0x41,
            HotkeyModifiers.Control,
            "Ctrl + A");
        var documents = new[]
        {
            CreateVersionTwoSound(soundA, "Alpha", "alpha.wav", 20, null),
            CreateVersionTwoSound(soundB, "Bravo", "bravo.mp3", 10, hotkey),
            CreateVersionTwoSound(soundC, "Charlie", "charlie.wav", 20, null),
            CreateVersionTwoSound(soundD, "Delta", "delta.mp3", 99, null)
        };
        foreach (var document in documents)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(soundsPath, document.ManagedFileName),
                [0x00]);
        }

        await WriteJsonAsync(
            Path.Combine(root, "library.json"),
            new
            {
                SchemaVersion = 2,
                Sounds = documents
            });

        await using var store = new SoundLibraryStore(root);
        var migrated = await store.LoadAsync();
        AssertEqual(4, migrated.Sounds.Count, "v2 sound count");
        AssertEqual(0, migrated.Categories.Count, "v2 category default");
        AssertTrue(
            migrated.Warnings.Any(
                warning => warning.Contains(
                    "schema version 2",
                    StringComparison.OrdinalIgnoreCase)),
            "migration warning");
        AssertSequence(
            [soundB, soundA, soundC, soundD],
            migrated.Sounds.Select(sound => sound.Id),
            "v2 current order preserved");
        AssertSequence(
            [0, 1, 2, 3],
            migrated.Sounds.Select(sound => sound.SortOrder),
            "sound order normalized");
        AssertTrue(
            migrated.Sounds.All(
                sound =>
                    sound.CategoryId is null
                    && !sound.IsFavorite
                    && sound.TileAccent == SoundTileAccent.Default),
            "v2 metadata defaults");
        AssertEqual(
            hotkey,
            migrated.Sounds.Single(sound => sound.Id == soundB).Hotkey,
            "v2 hotkey preserved");
        AssertEqual(
            "Bravo",
            migrated.Sounds.Single(sound => sound.Id == soundB).DisplayName,
            "v2 name preserved");
        AssertEqual(
            "bravo.mp3",
            migrated.Sounds.Single(sound => sound.Id == soundB)
                .OriginalFileName,
            "v2 filename preserved");
        AssertEqual(
            TimeSpan.FromSeconds(2),
            migrated.Sounds.Single(sound => sound.Id == soundB).Duration,
            "v2 duration preserved");

        using (var migratedJson = JsonDocument.Parse(
                   await File.ReadAllTextAsync(store.LibraryFilePath)))
        {
            AssertEqual(
                3,
                migratedJson.RootElement
                    .GetProperty("schemaVersion")
                    .GetInt32(),
                "schema persisted as v3");
        }

        var categoryOne = await store.CreateCategoryAsync("  Effects  ");
        AssertEqual("Effects", categoryOne.DisplayName, "category trimmed");
        await AssertThrowsAsync<InvalidOperationException>(
            () => store.CreateCategoryAsync("effects"),
            "case-insensitive duplicate category");
        var categoryTwo = await store.CreateCategoryAsync("Memes");
        var categoryThree = await store.CreateCategoryAsync("Music");
        var temporarilyCategorized = await store.UpdateSoundAsync(
            soundA,
            new SoundMetadataUpdate(
                "Alpha",
                categoryTwo.Id,
                false,
                SoundTileAccent.Default));
        AssertEqual(
            categoryTwo.Id,
            temporarilyCategorized.CategoryId,
            "sound assigned to category");
        var movedBack = await store.UpdateSoundAsync(
            soundA,
            new SoundMetadataUpdate(
                "Alpha",
                null,
                false,
                SoundTileAccent.Default));
        AssertEqual(
            null,
            movedBack.CategoryId,
            "sound moved back to Uncategorized");
        var renamed = await store.RenameCategoryAsync(
            categoryOne.Id,
            "Reactions");
        AssertEqual("Reactions", renamed.DisplayName, "category rename");

        var reorderedCategories = await store.ReorderCategoriesAsync(
            [categoryThree.Id, categoryOne.Id, categoryTwo.Id]);
        AssertSequence(
            [categoryThree.Id, categoryOne.Id, categoryTwo.Id],
            reorderedCategories.Select(category => category.Id),
            "category reorder");
        AssertSequence(
            [0, 1, 2],
            reorderedCategories.Select(category => category.SortOrder),
            "category order normalized");

        await store.UpdateSoundAsync(
            soundB,
            new SoundMetadataUpdate(
                "Bravo edited",
                categoryOne.Id,
                true,
                SoundTileAccent.Purple));
        await store.UpdateSoundAsync(
            soundC,
            new SoundMetadataUpdate(
                "Charlie",
                categoryOne.Id,
                false,
                SoundTileAccent.Teal));

        var categoryReorder = await store.ReorderSoundsAsync(
            [soundC, soundB],
            categoryOne.Id,
            allSounds: false);
        AssertSequence(
            [soundC, soundA, soundB, soundD],
            categoryReorder.Select(sound => sound.Id),
            "category sound reorder preserves outside slots");

        var uncategorizedReorder = await store.ReorderSoundsAsync(
            [soundD, soundA],
            categoryId: null,
            allSounds: false);
        AssertSequence(
            [soundC, soundD, soundB, soundA],
            uncategorizedReorder.Select(sound => sound.Id),
            "Uncategorized sound reorder");

        var allReorder = await store.ReorderSoundsAsync(
            [soundA, soundB, soundD, soundC],
            categoryId: null,
            allSounds: true);
        AssertSequence(
            [soundA, soundB, soundD, soundC],
            allReorder.Select(sound => sound.Id),
            "All Sounds reorder");
        AssertEqual(
            soundB,
            allReorder.Single(sound => sound.Hotkey == hotkey).Id,
            "hotkey remains on stable sound ID");

        var playingTile = new SoundTileViewModel(
            allReorder.Single(sound => sound.Id == soundB),
            "Reactions")
        {
            IsPlaying = true
        };
        var editedPlayingSound = await store.UpdateSoundAsync(
            soundB,
            new SoundMetadataUpdate(
                "Bravo playing",
                categoryOne.Id,
                true,
                SoundTileAccent.Red));
        playingTile.ReplaceSound(editedPlayingSound, "Reactions");
        AssertTrue(playingTile.IsPlaying, "playing state survives edit");
        AssertEqual(soundB, playingTile.Id, "playing stable ID survives edit");

        var orderBeforeDelete = allReorder
            .Select(sound => sound.Id)
            .ToArray();
        var managedFilesBeforeDelete = Directory
            .GetFiles(soundsPath)
            .Order()
            .ToArray();
        var deletion = await store.DeleteCategoryAsync(categoryOne.Id);
        AssertEqual(
            2,
            deletion.UncategorizedSoundCount,
            "category delete affected count");
        AssertTrue(
            deletion.Sounds
                .Where(sound => sound.Id is var id
                    && (id == soundB || id == soundC))
                .All(sound => sound.CategoryId is null),
            "category delete moves sounds to Uncategorized");
        AssertSequence(
            orderBeforeDelete,
            deletion.Sounds.Select(sound => sound.Id),
            "category delete preserves sound order");
        AssertSequence(
            managedFilesBeforeDelete,
            Directory.GetFiles(soundsPath).Order(),
            "category delete preserves managed files");

        await using (var reopened = new SoundLibraryStore(root))
        {
            var persisted = await reopened.LoadAsync();
            AssertSequence(
                orderBeforeDelete,
                persisted.Sounds.Select(sound => sound.Id),
                "sound order persists after reopen");
            AssertSequence(
                [categoryThree.Id, categoryTwo.Id],
                persisted.Categories.Select(category => category.Id),
                "category order persists after reopen");
            var persistedFavorite = persisted.Sounds.Single(
                sound => sound.Id == soundB);
            AssertTrue(
                persistedFavorite.IsFavorite,
                "favorite persists after reopen");
            AssertEqual(
                SoundTileAccent.Red,
                persistedFavorite.TileAccent,
                "tile accent persists after reopen");
            AssertEqual(
                "Bravo playing",
                persistedFavorite.DisplayName,
                "sound edit persists after reopen");
        }

        var failSave = false;
        await using var failingStore = new SoundLibraryStore(
            root,
            _ => failSave
                ? Task.FromException(
                    new IOException("Controlled save failure."))
                : Task.CompletedTask);
        var beforeFailure = await failingStore.LoadAsync();
        var jsonBeforeFailure = await File.ReadAllTextAsync(
            failingStore.LibraryFilePath);
        failSave = true;
        await AssertThrowsAsync<IOException>(
            () => failingStore.ReorderSoundsAsync(
                beforeFailure.Sounds
                    .Select(sound => sound.Id)
                    .Reverse()
                    .ToArray(),
                categoryId: null,
                allSounds: true),
            "controlled reorder save failure");
        AssertEqual(
            jsonBeforeFailure,
            await File.ReadAllTextAsync(failingStore.LibraryFilePath),
            "failed save leaves disk order unchanged");

        Console.WriteLine(
            "PASS migration, category CRUD, metadata, ordering, persistence, "
            + "stable IDs, and rollback");
    }

    private static async Task RunImportAndSearchTestsAsync(string root)
    {
        Directory.CreateDirectory(root);
        await using var store = new SoundLibraryStore(root);
        _ = await store.LoadAsync();
        var category = await store.CreateCategoryAsync("Reactions");
        var source = Path.Combine(root, "Original Reaction.wav");
        WriteTestWave(source);

        var import = await store.ImportAsync([source]);
        AssertEqual(1, import.Imported.Count, "new import count");
        var sound = import.Imported[0];
        AssertTrue(
            sound.CategoryId is null
            && !sound.IsFavorite
            && sound.TileAccent == SoundTileAccent.Default,
            "new import defaults");
        AssertEqual(0, sound.SortOrder, "new import appended");

        var duplicate = await store.ImportAsync([source]);
        AssertEqual(0, duplicate.Imported.Count, "duplicate import skipped");
        AssertEqual(1, duplicate.Duplicates.Count, "duplicate detected");

        var categorized = await store.UpdateSoundAsync(
            sound.Id,
            new SoundMetadataUpdate(
                "Crowd Cheer",
                category.Id,
                true,
                SoundTileAccent.Green));
        AssertTrue(
            SoundLibraryFilter.MatchesSearch(
                categorized,
                category.DisplayName,
                "crowd"),
            "search display name");
        AssertTrue(
            SoundLibraryFilter.MatchesSearch(
                categorized,
                category.DisplayName,
                "original reaction.wav"),
            "search original filename");
        AssertTrue(
            SoundLibraryFilter.MatchesSearch(
                categorized,
                category.DisplayName,
                "reactions"),
            "search category name");

        var hiddenView = new LibraryViewItem(
            SoundLibraryViewKind.Uncategorized,
            "Uncategorized");
        AssertTrue(
            !SoundLibraryFilter.MatchesView(categorized, hiddenView),
            "category filter hides sound");
        var favoritesView = new LibraryViewItem(
            SoundLibraryViewKind.Favorites,
            "Favorites");
        AssertTrue(
            SoundLibraryFilter.MatchesView(categorized, favoritesView),
            "Favorites view includes favorite immediately");
        AssertTrue(
            !SoundLibraryFilter.CanReorder(favoritesView, searchText: null),
            "reorder disabled in Favorites");
        AssertTrue(
            !SoundLibraryFilter.CanReorder(
                new LibraryViewItem(
                    SoundLibraryViewKind.AllSounds,
                    "All Sounds"),
                "cheer"),
            "reorder disabled during search");
        AssertTrue(
            SoundLibraryFilter.CanReorder(
                new LibraryViewItem(
                    SoundLibraryViewKind.Category,
                    "Reactions",
                    category.Id),
                searchText: null),
            "reorder enabled in category");

        var hotkey = new HotkeyGesture(
            0x42,
            HotkeyModifiers.Control,
            "Ctrl + B");
        var withHotkey = await store.UpdateHotkeyAsync(
            categorized.Id,
            hotkey);
        AssertEqual(
            hotkey,
            withHotkey.Hotkey,
            "filtered sound retains hotkey metadata");

        Console.WriteLine(
            "PASS import defaults, duplicate detection, search, filtering, "
            + "and hidden-sound hotkey metadata");
    }

    private static async Task RunInvalidMetadataFallbackTestAsync(string root)
    {
        var soundsPath = Path.Combine(root, "Sounds");
        Directory.CreateDirectory(soundsPath);
        var soundId = Guid.NewGuid();
        var managedFileName = $"{soundId:N}.wav";
        await File.WriteAllBytesAsync(
            Path.Combine(soundsPath, managedFileName),
            [0x00]);
        await WriteJsonAsync(
            Path.Combine(root, "library.json"),
            new
            {
                SchemaVersion = 3,
                Categories = Array.Empty<object>(),
                Sounds = new[]
                {
                    new
                    {
                        Id = soundId,
                        DisplayName = "Fallback",
                        ManagedFileName = managedFileName,
                        OriginalFileName = "fallback.wav",
                        FileType = "WAV",
                        Duration = TimeSpan.FromSeconds(1),
                        ImportedAtUtc = DateTimeOffset.UtcNow,
                        SortOrder = -10,
                        ContentHash = "ABC123",
                        Hotkey = (object?)null,
                        CategoryId = (object)Guid.NewGuid().ToString(),
                        IsFavorite = (object)"not-a-boolean",
                        TileAccent = (object)"Laser"
                    }
                }
            });

        await using var store = new SoundLibraryStore(root);
        var loaded = await store.LoadAsync();
        AssertEqual(1, loaded.Sounds.Count, "invalid new metadata keeps sound");
        AssertTrue(
            loaded.Sounds[0].CategoryId is null
            && !loaded.Sounds[0].IsFavorite
            && loaded.Sounds[0].TileAccent
                == SoundTileAccent.Default
            && loaded.Sounds[0].SortOrder == 0,
            "invalid new metadata safe defaults");
        AssertTrue(
            loaded.Warnings.Count >= 3,
            "invalid new metadata warnings");
        Console.WriteLine("PASS invalid metadata safe fallback");
    }

    private static async Task RunVersionOneMigrationTestAsync(string root)
    {
        var soundsPath = Path.Combine(root, "Sounds");
        Directory.CreateDirectory(soundsPath);
        var soundId = Guid.NewGuid();
        var document = CreateVersionTwoSound(
            soundId,
            "Version one",
            "version-one.wav",
            0,
            null);
        await File.WriteAllBytesAsync(
            Path.Combine(soundsPath, document.ManagedFileName),
            [0x00]);
        await WriteJsonAsync(
            Path.Combine(root, "library.json"),
            new
            {
                Sounds = new[] { document }
            });

        await using var store = new SoundLibraryStore(root);
        var loaded = await store.LoadAsync();
        AssertEqual(1, loaded.Sounds.Count, "v1 library loads");
        AssertTrue(
            loaded.Warnings.Any(
                warning => warning.Contains(
                    "schema version 1",
                    StringComparison.OrdinalIgnoreCase)),
            "v1 migration warning");
        Console.WriteLine("PASS version 1 migration");
    }

    private static VersionTwoSoundDocument CreateVersionTwoSound(
        Guid id,
        string displayName,
        string originalFileName,
        int sortOrder,
        HotkeyGesture? hotkey)
    {
        return new VersionTwoSoundDocument(
            id,
            displayName,
            $"{id:N}{Path.GetExtension(originalFileName)}",
            originalFileName,
            Path.GetExtension(originalFileName)
                .TrimStart('.')
                .ToUpperInvariant(),
            TimeSpan.FromSeconds(2),
            DateTimeOffset.UtcNow,
            sortOrder,
            Convert.ToHexString(id.ToByteArray()),
            hotkey);
    }

    private static async Task WriteJsonAsync(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
    }

    private static void WriteTestWave(string path)
    {
        const int sampleRate = 8000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int sampleCount = 800;
        const int dataLength =
            sampleCount * channels * bitsPerSample / 8;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);
        for (var index = 0; index < sampleCount; index++)
        {
            writer.Write((short)0);
        }
    }

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed ({message}): expected "
            + $"{typeof(TException).Name}.");
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(
                $"Assertion failed: {message}.");
        }
    }

    private static void AssertEqual<T>(
        T expected,
        T actual,
        string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Assertion failed ({message}): expected \"{expected}\", "
                + $"actual \"{actual}\".");
        }
    }

    private static void AssertSequence<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"Assertion failed ({message}): expected "
                + $"[{string.Join(", ", expected)}], actual "
                + $"[{string.Join(", ", actual)}].");
        }
    }

    private static void TryDeleteTestDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            Console.Error.WriteLine(
                $"Test cleanup could not remove {path}.");
        }
    }

    private sealed record VersionTwoSoundDocument(
        Guid Id,
        string DisplayName,
        string ManagedFileName,
        string OriginalFileName,
        string FileType,
        TimeSpan Duration,
        DateTimeOffset ImportedAtUtc,
        int SortOrder,
        string ContentHash,
        HotkeyGesture? Hotkey);
}
