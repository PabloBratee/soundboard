using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;
using Soundboard.App.Hotkeys;
using Soundboard.App.Lifetime;
using Soundboard.App.Presentation;
using Soundboard.App.Storage;
using Soundboard.Audio;

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
            $"Soundboard-Milestone9-Tests-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(testRoot);
            RunSingleInstanceTests();
            await RunMigrationAndOrganizationTestsAsync(
                Path.Combine(testRoot, "organization"));
            await RunImportAndSearchTestsAsync(
                Path.Combine(testRoot, "import"));
            await RunInvalidMetadataFallbackTestAsync(
                Path.Combine(testRoot, "invalid-metadata"));
            await RunFormatMetadataFallbackAndFutureSchemaTestAsync(
                Path.Combine(testRoot, "format-metadata"));
            await RunVersionOneMigrationTestAsync(
                Path.Combine(testRoot, "version-one"));
            await RunAudioCompatibilityTestsAsync(
                Path.Combine(testRoot, "audio-formats"));
            await RunClipMetadataTestsAsync(
                Path.Combine(testRoot, "clip-metadata"));
            await RunClipProviderTestsAsync(
                Path.Combine(testRoot, "clip-providers"));
            await RunWaveformTestsAsync(
                Path.Combine(testRoot, "waveforms"));
            RunPreviewSafetyTests();
            await RunLoudnessAndLimiterTestsAsync(
                Path.Combine(testRoot, "loudness-limiter"));
            await RunVersionFiveAndSettingsMigrationTestsAsync(
                Path.Combine(testRoot, "v5-settings"));
            await RunOptionalLocalFormatTestsAsync(
                Path.Combine(testRoot, "local-formats"));
            Console.WriteLine("All Milestone 9 tests passed.");
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

    private static void RunSingleInstanceTests()
    {
        var mutexName =
            $@"Local\Pablo.Soundboard.Tests.{Guid.NewGuid():N}";

        AssertTrue(
            SingleInstanceGuard.TryAcquire(mutexName, out var first),
            "first process acquires the single-instance mutex");
        AssertTrue(
            !SingleInstanceGuard.TryAcquire(mutexName, out var second),
            "second process is rejected by the single-instance mutex");
        AssertEqual(
            null,
            second,
            "rejected process does not retain a mutex guard");

        first!.Dispose();
        first.Dispose();

        AssertTrue(
            SingleInstanceGuard.TryAcquire(mutexName, out var afterRelease),
            "mutex is available after clean shutdown");
        afterRelease!.Dispose();

        Console.WriteLine(
            "PASS single-instance acquisition, rejection, and release");
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
                6,
                migratedJson.RootElement
                    .GetProperty("schemaVersion")
                    .GetInt32(),
                "schema persisted as v6");
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
        AssertOneShotEndOfStream(source, "WAV");

        var import = await store.ImportAsync([source]);
        AssertEqual(1, import.Imported.Count, "new import count");
        var sound = import.Imported[0];
        AssertTrue(
            sound.CategoryId is null
            && !sound.IsFavorite
            && sound.TileAccent == SoundTileAccent.Default
            && sound.TrimStartMilliseconds == 0
            && sound.TrimEndMilliseconds is null
            && sound.FadeInMilliseconds == 0
            && sound.FadeOutMilliseconds == 0,
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

    private static async Task RunAudioCompatibilityTestsAsync(string root)
    {
        var sources = Path.Combine(root, "Sources");
        Directory.CreateDirectory(sources);
        var monoOpus = Path.Combine(sources, "discord-mono.ogg");
        var stereoOpus = Path.Combine(sources, "stereo.opus");
        WriteTestOpus(monoOpus, channels: 1);
        WriteTestOpus(stereoOpus, channels: 2);

        var vorbis = Path.Combine(sources, "tiny-vorbis.ogg");
        File.Copy(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "tiny-vorbis.ogg"),
            vorbis);

        var monoDetails = AudioFileInspector.Inspect(monoOpus);
        AssertEqual(
            AudioCodecType.Opus,
            monoDetails.Format.Codec,
            "Ogg Opus codec detection");
        AssertEqual(1, monoDetails.Channels, "mono Opus channel count");
        AssertEqual(48000, monoDetails.SampleRate, "Opus sample rate");
        AssertEqual(
            "OGG · Opus",
            monoDetails.Format.DisplayLabel,
            "Ogg Opus label");
        using (var initial = AudioFileDecoderFactory.Default.Open(monoOpus))
        {
            var initialSamples = new float[256];
            var initialRead = initial.SampleProvider.Read(
                initialSamples,
                0,
                initialSamples.Length);
            using var restarted = initial.Restart();
            var restartedSamples = new float[256];
            var restartedRead = restarted.SampleProvider.Read(
                restartedSamples,
                0,
                restartedSamples.Length);
            AssertEqual(initialRead, restartedRead, "fresh restart length");
            AssertSequence(
                initialSamples.Take(initialRead),
                restartedSamples.Take(restartedRead),
                "fresh restart returns to beginning");
        }

        var stereoDetails = AudioFileInspector.Inspect(stereoOpus);
        AssertEqual(
            AudioCodecType.Opus,
            stereoDetails.Format.Codec,
            ".opus codec detection");
        AssertEqual(2, stereoDetails.Channels, "stereo Opus channel count");
        AssertEqual(
            ".opus",
            stereoDetails.Format.OriginalExtension,
            ".opus extension metadata");

        var vorbisDetails = AudioFileInspector.Inspect(vorbis);
        AssertEqual(
            AudioCodecType.Vorbis,
            vorbisDetails.Format.Codec,
            "Ogg Vorbis codec detection");
        AssertTrue(
            vorbisDetails.Channels is 1 or 2,
            "Vorbis normal channel count");
        AssertEqual(
            "OGG · Vorbis",
            vorbisDetails.Format.DisplayLabel,
            "Ogg Vorbis label");

        var vorbisNamedOpus = Path.Combine(sources, "vorbis-mismatch.opus");
        File.Copy(vorbis, vorbisNamedOpus);
        var mismatchDetails = AudioFileInspector.Inspect(vorbisNamedOpus);
        AssertEqual(
            AudioCodecType.Vorbis,
            mismatchDetails.Format.Codec,
            "Vorbis content named .opus detected by content");

        var trimmedOpus = Path.Combine(sources, "preskip.ogg");
        var trimmedBytes = File.ReadAllBytes(monoOpus);
        SetOpusPreSkip(trimmedBytes, 312);
        SetFinalOggGranule(trimmedBytes, 4800);
        File.WriteAllBytes(trimmedOpus, trimmedBytes);
        using (var trimmedSource =
               AudioFileDecoderFactory.Default.Open(trimmedOpus))
        {
            var trimmedSamples = ReadAllSamples(trimmedSource);
            AssertEqual(
                4488L,
                trimmedSamples,
                "Opus pre-skip is removed from decoded output");
        }

        AssertOneShotEndOfStream(monoOpus, "mono Ogg Opus");
        AssertOneShotEndOfStream(stereoOpus, "stereo .opus");
        AssertOneShotEndOfStream(vorbis, "Ogg Vorbis");

        var corrupt = Path.Combine(sources, "corrupt.ogg");
        var validBytes = File.ReadAllBytes(monoOpus);
        File.WriteAllBytes(corrupt, validBytes[..(validBytes.Length / 2)]);
        AssertThrows<InvalidDataException>(
            () => AudioFileInspector.Inspect(corrupt),
            "truncated Ogg rejected");

        var corruptOpusPacket = Path.Combine(
            sources,
            "corrupt-opus-packet.ogg");
        var corruptOpusBytes = File.ReadAllBytes(monoOpus);
        CorruptFirstPacketOnPage(corruptOpusBytes, pageIndex: 2);
        File.WriteAllBytes(corruptOpusPacket, corruptOpusBytes);
        AssertThrows<InvalidDataException>(
            () => AudioFileInspector.Inspect(corruptOpusPacket),
            "corrupt Opus packet rejected");

        var corruptVorbisData = Path.Combine(
            sources,
            "corrupt-vorbis-data.ogg");
        var corruptVorbisBytes = File.ReadAllBytes(vorbis);
        CorruptVorbisSetupPacket(corruptVorbisBytes);
        File.WriteAllBytes(corruptVorbisData, corruptVorbisBytes);
        AssertThrows<InvalidDataException>(
            () => AudioFileInspector.Inspect(corruptVorbisData),
            "corrupt Vorbis data rejected");

        var renamed = Path.Combine(sources, "renamed.ogg");
        File.WriteAllBytes(renamed, "not an Ogg file"u8.ToArray());
        AssertThrows<InvalidDataException>(
            () => AudioFileInspector.Inspect(renamed),
            "non-Ogg data rejected");

        var unsupported = Path.Combine(sources, "unsupported.ogg");
        var unsupportedBytes = File.ReadAllBytes(monoOpus);
        ReplaceFirstPacketSignature(unsupportedBytes, "BadCodec"u8);
        File.WriteAllBytes(unsupported, unsupportedBytes);
        AssertThrows<NotSupportedException>(
            () => AudioFileInspector.Inspect(unsupported),
            "unsupported Ogg codec rejected");

        var multichannel = Path.Combine(sources, "multichannel.ogg");
        var multichannelBytes = File.ReadAllBytes(monoOpus);
        SetOpusChannelCount(multichannelBytes, 3);
        File.WriteAllBytes(multichannel, multichannelBytes);
        AssertThrows<NotSupportedException>(
            () => AudioFileInspector.Inspect(multichannel),
            "multichannel Ogg rejected");

        var empty = Path.Combine(sources, "empty.ogg");
        File.WriteAllBytes(empty, []);
        AssertThrows<InvalidDataException>(
            () => AudioFileInspector.Inspect(empty),
            "zero-length Ogg rejected");

        await using (var store = new SoundLibraryStore(root))
        {
            _ = await store.LoadAsync();
            var category = await store.CreateCategoryAsync("Discord");
            var import = await store.ImportAsync(
                [
                    monoOpus,
                    stereoOpus,
                    vorbis,
                    corrupt,
                    corruptOpusPacket,
                    corruptVorbisData,
                    renamed,
                    empty
                ]);
            AssertEqual(3, import.Imported.Count, "Ogg import count");
            AssertEqual(5, import.InvalidFiles.Count, "invalid Ogg count");
            AssertTrue(
                import.Imported.Any(
                    sound =>
                        sound.Codec == AudioCodecType.Opus
                        && sound.OriginalExtension == ".ogg"),
                "Ogg Opus metadata stored");
            AssertTrue(
                import.Imported.Any(
                    sound =>
                        sound.Codec == AudioCodecType.Opus
                        && sound.OriginalExtension == ".opus"),
                ".opus metadata stored");
            AssertTrue(
                import.Imported.Any(
                    sound => sound.Codec == AudioCodecType.Vorbis),
                "Vorbis metadata stored");

            var exactDuplicate = await store.ImportAsync([monoOpus]);
            AssertEqual(
                1,
                exactDuplicate.Duplicates.Count,
                "exact Ogg duplicate detected");
            AssertEqual(
                3,
                Directory.GetFiles(store.SoundsPath).Length,
                "duplicate creates no managed copy");

            var opusSound = import.Imported.Single(
                sound =>
                    sound.Codec == AudioCodecType.Opus
                    && sound.OriginalExtension == ".ogg");
            var updated = await store.UpdateSoundAsync(
                opusSound.Id,
                new SoundMetadataUpdate(
                    opusSound.DisplayName,
                    category.Id,
                    true,
                    SoundTileAccent.Purple));
            var hotkey = new HotkeyGesture(
                0x47,
                HotkeyModifiers.Control,
                "Ctrl + G");
            _ = await store.UpdateHotkeyAsync(updated.Id, hotkey);
        }

        await using (var reloadedStore = new SoundLibraryStore(root))
        {
            var reloaded = await reloadedStore.LoadAsync();
            AssertEqual(3, reloaded.Sounds.Count, "Ogg persistence count");
            AssertTrue(
                reloaded.Sounds.All(
                    sound =>
                        sound.Container == AudioContainerType.Ogg
                        && sound.FormatLabel.StartsWith(
                            "OGG · ",
                            StringComparison.Ordinal)),
                "format labels persist");
            var personalized = reloaded.Sounds.Single(
                sound => sound.Hotkey is not null);
            AssertTrue(
                personalized.CategoryId is not null
                && personalized.IsFavorite
                && personalized.TileAccent == SoundTileAccent.Purple,
                "Ogg stable ID personalization persists");
        }

        Console.WriteLine(
            "PASS Ogg Opus, Ogg Vorbis, .opus, mono/stereo, one-shot EOS, "
            + "invalid/corrupt/multichannel rejection, duplicate detection, "
            + "schema v6 format persistence, and personalization");
    }

    private static async Task RunOptionalLocalFormatTestsAsync(string root)
    {
        var mp3Path = Environment.GetEnvironmentVariable(
            "SOUNDBOARD_MP3_TEST_FILE");
        if (!string.IsNullOrWhiteSpace(mp3Path) && File.Exists(mp3Path))
        {
            var details = AudioFileInspector.Inspect(mp3Path);
            AssertEqual(
                AudioCodecType.MpegLayer3,
                details.Format.Codec,
                "local MP3 codec");
            AssertOneShotEndOfStream(mp3Path, "local MP3");
            await using var store = new SoundLibraryStore(
                Path.Combine(root, "mp3"));
            _ = await store.LoadAsync();
            var import = await store.ImportAsync([mp3Path]);
            AssertEqual(1, import.Imported.Count, "local MP3 import");
            Console.WriteLine(
                $"PASS local MP3 import and one-shot decode: "
                + $"{Path.GetFileName(mp3Path)}");
        }
        else
        {
            Console.WriteLine(
                "SKIP optional local MP3: "
                + "SOUNDBOARD_MP3_TEST_FILE was not set.");
        }

        var discordOggPath = Environment.GetEnvironmentVariable(
            "SOUNDBOARD_DISCORD_OGG_TEST_FILE");
        if (!string.IsNullOrWhiteSpace(discordOggPath)
            && File.Exists(discordOggPath))
        {
            var details = AudioFileInspector.Inspect(discordOggPath);
            AssertTrue(
                details.Format.Codec
                    is AudioCodecType.Opus or AudioCodecType.Vorbis,
                "local Discord Ogg codec");
            AssertOneShotEndOfStream(
                discordOggPath,
                "local Discord Ogg");
            Console.WriteLine(
                $"PASS local Discord Ogg content detection and one-shot "
                + $"decode: {Path.GetFileName(discordOggPath)} "
                + $"({details.Format.DisplayLabel})");
        }
        else
        {
            Console.WriteLine(
                "SKIP optional real Discord Ogg: "
                + "SOUNDBOARD_DISCORD_OGG_TEST_FILE was not set.");
        }
    }

    private static async Task RunClipMetadataTestsAsync(string root)
    {
        var soundsPath = Path.Combine(root, "Sounds");
        Directory.CreateDirectory(soundsPath);
        var soundId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var managedFileName = $"{soundId:N}.wav";
        var managedPath = Path.Combine(soundsPath, managedFileName);
        WriteEditingWave(managedPath, channels: 1);
        var hash = await GetFileHashAsync(managedPath);
        var importedAt = DateTimeOffset.UtcNow;
        var hotkey = new HotkeyGesture(
            0x48,
            HotkeyModifiers.Control,
            "Ctrl + H");
        await WriteJsonAsync(
            Path.Combine(root, "library.json"),
            new
            {
                SchemaVersion = 4,
                Categories = new[]
                {
                    new
                    {
                        Id = categoryId,
                        DisplayName = "Clips",
                        SortOrder = 0,
                        CreatedAtUtc = importedAt
                    }
                },
                Sounds = new[]
                {
                    new
                    {
                        Id = soundId,
                        DisplayName = "Metadata clip",
                        ManagedFileName = managedFileName,
                        OriginalFileName = "source.wav",
                        FileType = "WAV",
                        Duration = TimeSpan.FromSeconds(1),
                        ImportedAtUtc = importedAt,
                        SortOrder = 0,
                        ContentHash = hash,
                        Hotkey = hotkey,
                        CategoryId = categoryId,
                        IsFavorite = true,
                        TileAccent = SoundTileAccent.Teal,
                        Container = AudioContainerType.Wav,
                        Codec = AudioCodecType.Pcm,
                        OriginalExtension = ".wav"
                    }
                }
            });

        await using (var store = new SoundLibraryStore(root))
        {
            var loaded = await store.LoadAsync();
            var migrated = loaded.Sounds.Single();
            AssertTrue(
                loaded.Warnings.Any(
                    warning => warning.Contains(
                        "schema version 4",
                        StringComparison.OrdinalIgnoreCase)),
                "v4 to v5 migration warning");
            AssertEqual(soundId, migrated.Id, "stable ID migrated");
            AssertEqual(hash, migrated.ContentHash, "content hash migrated");
            AssertEqual(hotkey, migrated.Hotkey, "hotkey migrated");
            AssertEqual(categoryId, migrated.CategoryId, "category migrated");
            AssertTrue(migrated.IsFavorite, "favorite migrated");
            AssertEqual(
                SoundTileAccent.Teal,
                migrated.TileAccent,
                "accent migrated");
            AssertEqual(0, migrated.TrimStartMilliseconds, "default trim start");
            AssertEqual(null, migrated.TrimEndMilliseconds, "default trim end");
            AssertEqual(
                TimeSpan.FromSeconds(1),
                migrated.EffectiveDuration,
                "default clip covers source");
            var libraryBeforeWaveform = await File.ReadAllTextAsync(
                store.LibraryFilePath);
            _ = await new WaveformCacheService(root)
                .GetOrCreateAsync(managedPath, hash, 200);
            AssertEqual(
                libraryBeforeWaveform,
                await File.ReadAllTextAsync(store.LibraryFilePath),
                "waveform generation does not rewrite library JSON");

            var edited = await store.UpdateClipSettingsAsync(
                soundId,
                new SoundClipMetadataUpdate(100, 900, 100, 150));
            AssertEqual(soundId, edited.Id, "edit preserves stable ID");
            AssertEqual(hash, edited.ContentHash, "edit preserves content hash");
            AssertEqual(hotkey, edited.Hotkey, "edit preserves hotkey");
            AssertEqual(categoryId, edited.CategoryId, "edit preserves category");
            AssertTrue(edited.IsFavorite, "edit preserves favorite");
            AssertEqual(
                SoundTileAccent.Teal,
                edited.TileAccent,
                "edit preserves accent");
            AssertEqual(
                hash,
                await GetFileHashAsync(managedPath),
                "clip edit preserves managed-file bytes");
            AssertEqual(
                TimeSpan.FromMilliseconds(800),
                edited.EffectiveDuration,
                "effective duration persisted");
        }

        using (var persisted = JsonDocument.Parse(
                   await File.ReadAllTextAsync(
                       Path.Combine(root, "library.json"))))
        {
            AssertEqual(
                6,
                persisted.RootElement.GetProperty("schemaVersion").GetInt32(),
                "v4 migrated to schema v6");
        }

        await using (var reloadedStore = new SoundLibraryStore(root))
        {
            var reloaded = (await reloadedStore.LoadAsync()).Sounds.Single();
            AssertEqual(100, reloaded.TrimStartMilliseconds, "trim start reload");
            AssertEqual(900, reloaded.TrimEndMilliseconds, "trim end reload");
            AssertEqual(100, reloaded.FadeInMilliseconds, "fade in reload");
            AssertEqual(150, reloaded.FadeOutMilliseconds, "fade out reload");
            var cachePath = new WaveformCacheService(root)
                .GetCacheFilePath(hash, 200);
            AssertTrue(File.Exists(cachePath), "waveform cache exists before remove");
            string analysisCachePath;
            var libraryBeforeAnalysis = await File.ReadAllTextAsync(
                reloadedStore.LibraryFilePath);
            await using (var analysisService =
                         new LoudnessAnalysisService(root))
            {
                var analysisKey = LoudnessAnalysisKey.Create(
                    reloaded.ContentHash,
                    reloaded.ClipSettings);
                var analysis = await analysisService.GetOrAnalyzeAsync(
                    analysisKey,
                    managedPath,
                    reloaded.ClipSettings);
                AssertTrue(
                    analysis.Result.IsValid,
                    "edited sound analysis valid before removal");
                analysisCachePath =
                    analysisService.GetCacheFilePath(analysisKey);
            }

            AssertEqual(
                libraryBeforeAnalysis,
                await File.ReadAllTextAsync(
                    reloadedStore.LibraryFilePath),
                "analysis generation does not rewrite library JSON");
            AssertTrue(
                File.Exists(analysisCachePath),
                "analysis cache exists before remove");
            var removeWarnings = await reloadedStore.RemoveAsync(soundId);
            AssertEqual(0, removeWarnings.Count, "waveform removal warnings");
            AssertTrue(!File.Exists(managedPath), "managed copy removed");
            AssertTrue(!File.Exists(cachePath), "waveform cache removed");
            AssertTrue(
                !File.Exists(analysisCachePath),
                "analysis cache removed");
        }

        var invalidRoot = Path.Combine(root, "invalid");
        var invalidSounds = Path.Combine(invalidRoot, "Sounds");
        Directory.CreateDirectory(invalidSounds);
        var invalidId = Guid.NewGuid();
        var invalidManaged = $"{invalidId:N}.wav";
        WriteEditingWave(
            Path.Combine(invalidSounds, invalidManaged),
            channels: 1);
        await WriteJsonAsync(
            Path.Combine(invalidRoot, "library.json"),
            new
            {
                SchemaVersion = 5,
                Categories = Array.Empty<object>(),
                Sounds = new[]
                {
                    new
                    {
                        Id = invalidId,
                        DisplayName = "Invalid clip",
                        ManagedFileName = invalidManaged,
                        OriginalFileName = "invalid.wav",
                        FileType = "WAV",
                        Duration = TimeSpan.FromSeconds(1),
                        ImportedAtUtc = importedAt,
                        SortOrder = 0,
                        ContentHash = new string('A', 64),
                        Container = AudioContainerType.Wav,
                        Codec = AudioCodecType.Pcm,
                        OriginalExtension = ".wav",
                        TrimStartMilliseconds = -5,
                        TrimEndMilliseconds = 50,
                        FadeInMilliseconds = int.MaxValue,
                        FadeOutMilliseconds = -1
                    }
                }
            });
        await using (var invalidStore = new SoundLibraryStore(invalidRoot))
        {
            var loaded = await invalidStore.LoadAsync();
            var preserved = loaded.Sounds.Single();
            AssertEqual(invalidId, preserved.Id, "invalid clip sound preserved");
            AssertEqual(0, preserved.TrimStartMilliseconds, "invalid trim reset");
            AssertEqual(null, preserved.TrimEndMilliseconds, "invalid end reset");
            AssertEqual(0, preserved.FadeInMilliseconds, "invalid fade in reset");
            AssertEqual(0, preserved.FadeOutMilliseconds, "invalid fade out reset");
            AssertTrue(
                loaded.Warnings.Any(
                    warning => warning.Contains(
                        "invalid clip edit metadata",
                        StringComparison.OrdinalIgnoreCase)),
                "invalid clip warning surfaced");
        }

        Console.WriteLine(
            "PASS schema v4-to-v6 migration, clip defaults, invalid metadata "
            + "fallback, persistence, and identity/personalization preservation");
    }

    private static Task RunClipProviderTestsAsync(string root)
    {
        Directory.CreateDirectory(root);
        const int sampleRate = 1000;
        var mono = new TestSampleProvider(
            Enumerable.Repeat(1f, sampleRate).ToArray(),
            sampleRate,
            channels: 1);
        var settings = AudioClipSettings.Create(
            TimeSpan.FromSeconds(1),
            200,
            800,
            100,
            100);
        var clipped = new AudioClipSampleProvider(mono, settings);
        var samples = ReadAllSamples(clipped);
        AssertEqual(600, samples.Length, "mono trimmed sample count");
        AssertTrue(Math.Abs(samples[0]) < 0.001f, "fade in starts near silence");
        AssertTrue(samples[99] > 0.99f, "fade in reaches full scale");
        AssertTrue(samples[500] > 0.99f, "fade out begins at full scale");
        AssertTrue(Math.Abs(samples[^1]) < 0.001f, "fade out ends at silence");
        AssertEqual(
            0,
            clipped.Read(new float[32], 0, 32),
            "trimmed provider remains at EOS");

        var stereoSource = new TestSampleProvider(
            Enumerable.Repeat(0.5f, sampleRate * 2).ToArray(),
            sampleRate,
            channels: 2);
        var stereoClip = new AudioClipSampleProvider(
            stereoSource,
            settings);
        var stereoSamples = ReadAllSamples(stereoClip);
        AssertEqual(1200, stereoSamples.Length, "stereo trimmed sample count");
        AssertTrue(
            Math.Abs(stereoSamples[0] - stereoSamples[1]) < 0.0001f,
            "stereo channels share fade gain");

        var zeroFadeSource = new TestSampleProvider(
            Enumerable.Repeat(0.25f, sampleRate).ToArray(),
            sampleRate,
            channels: 1);
        var zeroFadeClip = new AudioClipSampleProvider(
            zeroFadeSource,
            AudioClipSettings.Create(
                TimeSpan.FromSeconds(1),
                100,
                900,
                0,
                0));
        AssertTrue(
            ReadAllSamples(zeroFadeClip).All(
                sample => Math.Abs(sample - 0.25f) < 0.0001f),
            "zero fades preserve samples");

        AssertThrows<ArgumentException>(
            () => AudioClipSettings.Create(
                TimeSpan.FromSeconds(1),
                0,
                99,
                0,
                0),
            "minimum 100 ms duration");
        AssertThrows<ArgumentException>(
            () => AudioClipSettings.Create(
                TimeSpan.FromSeconds(1),
                0,
                500,
                300,
                201),
            "fade sum validation");
        AssertThrows<OverflowException>(
            () => AudioSamplePosition.TimeToFramePosition(
                TimeSpan.MaxValue,
                int.MaxValue),
            "sample position overflow is checked");

        Console.WriteLine(
            "PASS sample-accurate trim boundaries, deterministic EOS, mono/"
            + "stereo fades, zero fades, minimum duration, fade sum, and "
            + "checked sample arithmetic");
        return Task.CompletedTask;
    }

    private static async Task RunWaveformTestsAsync(string root)
    {
        var sources = Path.Combine(root, "Sources");
        var cacheRoot = Path.Combine(root, "Cache");
        Directory.CreateDirectory(sources);
        var wav = Path.Combine(sources, "editing.wav");
        var stereoWav = Path.Combine(sources, "editing-stereo.wav");
        var mp3 = Path.Combine(sources, "editing.mp3");
        var opusOgg = Path.Combine(sources, "editing-opus.ogg");
        var opusExtension = Path.Combine(sources, "editing.opus");
        var vorbis = Path.Combine(sources, "editing-vorbis.ogg");
        WriteEditingWave(wav, channels: 1);
        WriteEditingWave(stereoWav, channels: 2);
        using (var reader = new WaveFileReader(wav))
        {
            MediaFoundationEncoder.EncodeToMp3(reader, mp3, 128000);
        }

        WriteTestOpus(opusOgg, channels: 1, frameCount: 48000);
        WriteTestOpus(opusExtension, channels: 2, frameCount: 48000);
        File.Copy(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "tiny-vorbis.ogg"),
            vorbis);

        var paths = new[]
        {
            (wav, "WAV"),
            (mp3, "MP3"),
            (opusOgg, "Ogg Opus"),
            (opusExtension, ".opus"),
            (vorbis, "Ogg Vorbis"),
            (stereoWav, "stereo WAV")
        };
        var cache = new WaveformCacheService(cacheRoot);
        foreach (var (path, label) in paths)
        {
            AssertDecodedTrim(path, label);
            var hash = await GetFileHashAsync(path);
            var waveform = await cache.GetOrCreateAsync(path, hash, 200);
            AssertEqual(200, waveform.Data.Peaks.Count, $"{label} bins");
            AssertTrue(
                waveform.Data.ChannelCount is 1 or 2,
                $"{label} waveform channel count");
            AssertTrue(
                waveform.Data.Peaks.All(
                    peak => float.IsFinite(peak) && peak >= 0f),
                $"{label} waveform peaks valid");
            var roundTrip = await new WaveformCacheService(cacheRoot)
                .GetOrCreateAsync(path, hash, 200);
            AssertTrue(roundTrip.LoadedFromCache, $"{label} cache round trip");
        }

        var wavHash = await GetFileHashAsync(wav);
        var corruptPath = cache.GetCacheFilePath(wavHash, 300);
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
        await File.WriteAllTextAsync(corruptPath, "{ corrupt");
        var regenerated = await new WaveformCacheService(cacheRoot)
            .GetOrCreateAsync(wav, wavHash, 300);
        AssertTrue(
            !regenerated.LoadedFromCache
            && regenerated.Warning?.Contains(
                "regenerated",
                StringComparison.OrdinalIgnoreCase) == true,
            "corrupt cache regenerated with warning");
        AssertTrue(
            WaveformCacheService.BuildCacheFileName(wavHash, 300, 1)
            != WaveformCacheService.BuildCacheFileName(wavHash, 300, 2),
            "cache key changes with waveform version");

        var invalid = Path.Combine(sources, "invalid.wav");
        await File.WriteAllBytesAsync(invalid, "not audio"u8.ToArray());
        await AssertThrowsAsync<Exception>(
            () => new WaveformCacheService(
                    Path.Combine(root, "FailureCache"))
                .GetOrCreateAsync(invalid, new string('B', 64), 200),
            "waveform failure surfaced");
        using var playbackAfterWaveformFailure =
            AudioFileDecoderFactory.Default.Open(wav);
        AssertTrue(
            playbackAfterWaveformFailure.SampleProvider.Read(
                new float[128],
                0,
                128) > 0,
            "waveform failure does not affect playback");

        using var virtualSource = AudioFileDecoderFactory.Default.Open(wav);
        using var monitorSource = AudioFileDecoderFactory.Default.Open(wav);
        var sharedSettings = AudioClipSettings.Create(
            virtualSource.Duration,
            100,
            900,
            100,
            100);
        AssertSequence(
            ReadAllSamples(
                new AudioClipSampleProvider(
                    virtualSource.SampleProvider,
                    sharedSettings)),
            ReadAllSamples(
                new AudioClipSampleProvider(
                    monitorSource.SampleProvider,
                    sharedSettings)),
            "independent virtual and monitor decoders use identical edits");

        Console.WriteLine(
            "PASS WAV, MP3, Ogg Opus, Ogg Vorbis, .opus, mono/stereo "
            + "waveforms and trims, cache round-trip/regeneration/versioning, "
            + "and waveform-failure playback isolation");
    }

    private static void RunPreviewSafetyTests()
    {
        var physical = new AudioEndpoint(
            "physical",
            "Headphones",
            AudioDeviceDirection.Render,
            AudioEndpointState.Active,
            IsDefault: true,
            IsLikelyVbCable: false);
        AudioPreviewService.ValidatePreviewEndpoint(physical);
        var cable = new AudioEndpoint(
            "cable",
            "CABLE Input (VB-Audio Virtual Cable)",
            AudioDeviceDirection.Render,
            AudioEndpointState.Active,
            IsDefault: false,
            IsLikelyVbCable: true);
        AssertThrows<InvalidOperationException>(
            () => AudioPreviewService.ValidatePreviewEndpoint(cable),
            "VB-CABLE rejected for preview");
        var capture = physical with
        {
            Direction = AudioDeviceDirection.Capture
        };
        AssertThrows<InvalidOperationException>(
            () => AudioPreviewService.ValidatePreviewEndpoint(capture),
            "capture endpoint rejected for preview");
        Console.WriteLine(
            "PASS preview endpoint safety accepts active physical render and "
            + "rejects VB-CABLE and capture endpoints");
    }

    private static async Task RunLoudnessAndLimiterTestsAsync(string root)
    {
        Directory.CreateDirectory(root);
        const int sampleRate = 48000;
        var analyzer = new LoudnessAnalyzer();
        var quietMono = CreateSineSamples(
            sampleRate, 1, 1.2, 0.1f);
        var loudMono = CreateSineSamples(
            sampleRate, 1, 1.2, 0.2f);
        var stereo = CreateSineSamples(
            sampleRate, 2, 1.2, 0.1f);
        var quietResult = analyzer.AnalyzeEffectiveClip(
            new TestSampleProvider(quietMono, sampleRate, 1),
            TimeSpan.FromSeconds(1.2));
        var loudResult = analyzer.AnalyzeEffectiveClip(
            new TestSampleProvider(loudMono, sampleRate, 1),
            TimeSpan.FromSeconds(1.2));
        var stereoResult = analyzer.AnalyzeEffectiveClip(
            new TestSampleProvider(stereo, sampleRate, 2),
            TimeSpan.FromSeconds(1.2));
        AssertTrue(quietResult.IsValid, "mono sine loudness is valid");
        AssertTrue(stereoResult.IsValid, "stereo sine loudness is valid");
        AssertTrue(
            loudResult.IntegratedLoudnessLufs
                > quietResult.IntegratedLoudnessLufs,
            "louder sine measures louder");
        AssertTrue(
            Math.Abs(
                loudResult.IntegratedLoudnessLufs
                - quietResult.IntegratedLoudnessLufs
                - 6.0206d) < 0.35d,
            "6 dB amplitude change measures approximately 6 LU");
        Console.WriteLine(
            $"INFO deterministic loudness fixtures: quiet "
            + $"{quietResult.IntegratedLoudnessLufs:N2} LUFS, loud "
            + $"{loudResult.IntegratedLoudnessLufs:N2} LUFS, stereo "
            + $"{stereoResult.IntegratedLoudnessLufs:N2} LUFS");

        var silence = analyzer.AnalyzeEffectiveClip(
            new TestSampleProvider(
                new float[sampleRate],
                sampleRate,
                1),
            TimeSpan.FromSeconds(1));
        AssertTrue(!silence.IsValid, "digital silence is non-normalizable");
        AssertTrue(
            silence.HasFiniteValues,
            "invalid analysis retains finite values");

        var changing = new float[sampleRate * 2];
        Array.Copy(
            CreateSineSamples(sampleRate, 1, 1, 0.03f),
            changing,
            sampleRate);
        Array.Copy(
            CreateSineSamples(sampleRate, 1, 1, 0.3f),
            0,
            changing,
            sampleRate,
            sampleRate);
        var sourceDuration = TimeSpan.FromSeconds(2);
        var firstHalf = AudioClipSettings.Create(
            sourceDuration, 0, 1000, 0, 0);
        var secondHalf = AudioClipSettings.Create(
            sourceDuration, 1000, null, 0, 0);
        var fadedSecondHalf = AudioClipSettings.Create(
            sourceDuration, 1000, null, 500, 0);
        var firstResult = AnalyzeEdited(
            analyzer, changing, sampleRate, firstHalf);
        var secondResult = AnalyzeEdited(
            analyzer, changing, sampleRate, secondHalf);
        var fadedResult = AnalyzeEdited(
            analyzer, changing, sampleRate, fadedSecondHalf);
        AssertTrue(
            secondResult.IntegratedLoudnessLufs
                > firstResult.IntegratedLoudnessLufs + 10d,
            "trim changes analysis input");
        AssertTrue(
            fadedResult.IntegratedLoudnessLufs
                < secondResult.IntegratedLoudnessLufs,
            "fade changes analysis input");

        var boost = LoudnessNormalization.Calculate(
            ValidAnalysis(-30d),
            -16d);
        var cut = LoudnessNormalization.Calculate(
            ValidAnalysis(10d),
            -16d);
        AssertEqual(12d, boost.AppliedGainDb, "+12 dB boost clamp");
        AssertEqual(-24d, cut.AppliedGainDb, "-24 dB cut clamp");
        AssertTrue(boost.WasClamped, "boost clamp warning state");
        AssertTrue(cut.WasClamped, "attenuation clamp warning state");
        AssertTrue(
            !LoudnessNormalization.Calculate(silence, -16d).IsAvailable,
            "invalid analysis never produces gain");

        var formatRoot = Path.Combine(root, "formats");
        Directory.CreateDirectory(formatRoot);
        var formatWave = Path.Combine(formatRoot, "tone.wav");
        var formatMp3 = Path.Combine(formatRoot, "tone.mp3");
        var formatOpus = Path.Combine(formatRoot, "tone.ogg");
        var formatOpusExtension = Path.Combine(formatRoot, "tone.opus");
        var formatVorbis = Path.Combine(formatRoot, "tone-vorbis.ogg");
        WriteSineWaveFile(formatWave, sampleRate, seconds: 1);
        using (var waveReader = new WaveFileReader(formatWave))
        {
            MediaFoundationEncoder.EncodeToMp3(
                waveReader,
                formatMp3,
                desiredBitRate: 128000);
        }

        WriteTestOpus(formatOpus, channels: 1, frameCount: sampleRate);
        WriteTestOpus(
            formatOpusExtension,
            channels: 2,
            frameCount: sampleRate);
        File.Copy(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "tiny-vorbis.ogg"),
            formatVorbis);
        foreach (var (path, label) in new[]
                 {
                     (formatWave, "WAV"),
                     (formatMp3, "MP3"),
                     (formatOpus, "Ogg Opus"),
                     (formatOpusExtension, ".opus"),
                     (formatVorbis, "Ogg Vorbis")
                 })
        {
            using var decoded =
                AudioFileDecoderFactory.Default.Open(path);
            LoudnessAnalysisResult result;
            if (label == "Ogg Vorbis"
                && decoded.Duration < TimeSpan.FromMilliseconds(400))
            {
                var decodedSamples = ReadAllSamples(decoded.SampleProvider);
                var repetitions = (int)Math.Ceiling(
                    sampleRate / (double)decodedSamples.Length);
                var repeated = Enumerable.Range(0, repetitions)
                    .SelectMany(_ => decodedSamples)
                    .Take(sampleRate)
                    .ToArray();
                result = analyzer.AnalyzeEffectiveClip(
                    new TestSampleProvider(repeated, sampleRate, 1),
                    TimeSpan.FromSeconds(1));
            }
            else
            {
                var settings =
                    AudioClipSettings.FullDuration(decoded.Duration);
                result = analyzer.AnalyzeFile(path, settings);
            }

            AssertTrue(result.IsValid, $"{label} loudness analysis");
        }

        var wavePath = Path.Combine(root, "analysis.wav");
        WriteEditingWave(wavePath, channels: 1);
        var contentHash = await GetFileHashAsync(wavePath);
        var fullSettings = AudioClipSettings.FullDuration(
            TimeSpan.FromSeconds(1));
        var key = LoudnessAnalysisKey.Create(contentHash, fullSettings);
        await using (var service = new LoudnessAnalysisService(root))
        {
            var requests = Enumerable.Range(0, 6)
                .Select(
                    _ => service.GetOrAnalyzeAsync(
                        key,
                        wavePath,
                        fullSettings))
                .ToArray();
            var outcomes = await Task.WhenAll(requests);
            AssertTrue(
                outcomes.All(outcome => outcome.Result.IsValid),
                "concurrent analysis results are valid");
            AssertEqual(
                1L,
                service.AnalysisExecutionCount,
                "identical analysis requests deduplicate");
            AssertTrue(
                (await service.TryLoadCachedAsync(key))?.LoadedFromCache
                    == true,
                "analysis cache round trip");

            await File.WriteAllTextAsync(
                service.GetCacheFilePath(key),
                "{ corrupt");
            var regenerated = await service.GetOrAnalyzeAsync(
                key,
                wavePath,
                fullSettings);
            AssertTrue(
                regenerated.Result.IsValid,
                "corrupt cache is regenerated");
            AssertTrue(
                regenerated.Warning?.Contains(
                    "corrupt",
                    StringComparison.OrdinalIgnoreCase) == true,
                "corrupt cache warning surfaced");
            AssertEqual(
                2L,
                service.AnalysisExecutionCount,
                "corrupt cache caused one regeneration");

            var trimmedKey = LoudnessAnalysisKey.Create(
                contentHash,
                AudioClipSettings.Create(
                    TimeSpan.FromSeconds(1),
                    100,
                    null,
                    0,
                    0));
            AssertTrue(key != trimmedKey, "trim changes analysis key");
        }

        RunLimiterTests(sampleRate);
        Console.WriteLine(
            "PASS loudness mono/stereo, gating, trim/fade sensitivity, "
            + "gain clamps, cache regeneration/deduplication, and limiter");
    }

    private static LoudnessAnalysisResult AnalyzeEdited(
        LoudnessAnalyzer analyzer,
        float[] source,
        int sampleRate,
        AudioClipSettings settings)
    {
        return analyzer.AnalyzeEffectiveClip(
            new AudioClipSampleProvider(
                new TestSampleProvider(source, sampleRate, 1),
                settings),
            settings.EffectiveDuration);
    }

    private static LoudnessAnalysisResult ValidAnalysis(double lufs)
    {
        return new LoudnessAnalysisResult(
            lufs,
            -1d,
            1d,
            LoudnessAnalyzer.AlgorithmVersion,
            true,
            null);
    }

    private static void RunLimiterTests(int sampleRate)
    {
        var below = CreateSineSamples(sampleRate, 1, 0.2, 0.2f);
        var belowLimiter = new SamplePeakLimiter(
            new TestSampleProvider(below, sampleRate, 1));
        var belowOutput = ReadAllSamples(belowLimiter);
        AssertEqual(below.Length, belowOutput.Length, "limiter mono EOS");
        var maximumDifference = below.Zip(belowOutput)
            .Max(pair => Math.Abs(pair.First - pair.Second));
        AssertTrue(
            maximumDifference < 0.00001f,
            $"below-ceiling signal is unchanged ({maximumDifference})");

        var hotStereo = Enumerable.Repeat(
            1f,
            sampleRate / 5 * 2).ToArray();
        var limiter = new SamplePeakLimiter(
            new TestSampleProvider(hotStereo, sampleRate, 2));
        var hotOutput = ReadAllSamples(limiter);
        var ceiling = (float)Math.Pow(10d, -1d / 20d);
        AssertEqual(hotStereo.Length, hotOutput.Length, "limiter stereo EOS");
        AssertTrue(
            hotOutput.All(float.IsFinite),
            "continuous maximum input remains finite");
        AssertTrue(
            hotOutput.Max(Math.Abs) <= ceiling + 0.00001f,
            "output stays below sample-peak ceiling");
        AssertTrue(
            limiter.MaximumGainReductionDb > 0.9f,
            "above-ceiling signal reports gain reduction");
        AssertEqual(
            SamplePeakLimiter.DefaultLookahead,
            limiter.AddedLatency,
            "reported limiter latency");
        Console.WriteLine(
            $"INFO limiter fixture: ceiling -1.0 dBFS, max reduction "
            + $"{limiter.MaximumGainReductionDb:N2} dB, latency "
            + $"{limiter.AddedLatency.TotalMilliseconds:N1} ms");

        var bypassInput = CreateSineSamples(sampleRate, 1, 0.05, 1f);
        var bypass = new SamplePeakLimiter(
            new TestSampleProvider(bypassInput, sampleRate, 1),
            enabled: false);
        AssertSequence(
            bypassInput,
            ReadAllSamples(bypass),
            "disabled limiter bypass");
        bypass.CeilingDbfs = -3d;
        AssertTrue(
            Math.Abs(bypass.CeilingDbfs + 3d) < 0.0001d,
            "ceiling update is safe");

        var nonFinite = new SamplePeakLimiter(
            new TestSampleProvider(
                [float.NaN, float.PositiveInfinity, 0.5f],
                sampleRate,
                1));
        AssertTrue(
            ReadAllSamples(nonFinite).All(float.IsFinite),
            "non-finite input is rejected");
        AssertEqual(
            2L,
            nonFinite.NonFiniteSampleCount,
            "non-finite diagnostic count");

        var releaseInput = Enumerable.Repeat(1f, sampleRate / 20)
            .Concat(Enumerable.Repeat(0.1f, sampleRate / 2))
            .ToArray();
        var releaseLimiter = new SamplePeakLimiter(
            new TestSampleProvider(releaseInput, sampleRate, 1));
        _ = ReadAllSamples(releaseLimiter);
        AssertTrue(
            releaseLimiter.MaximumGainReductionDb
                > releaseLimiter.CurrentGainReductionDb,
            "limiter release recovers smoothly toward unity");

        var toggleInput = CreateSineSamples(
            sampleRate,
            1,
            0.1,
            1f);
        var toggleLimiter = new SamplePeakLimiter(
            new TestSampleProvider(toggleInput, sampleRate, 1));
        var firstToggleBuffer = new float[500];
        var firstToggleRead = toggleLimiter.Read(
            firstToggleBuffer,
            0,
            firstToggleBuffer.Length);
        toggleLimiter.Enabled = false;
        var remainingToggleOutput = ReadAllSamples(toggleLimiter);
        AssertEqual(
            toggleInput.Length,
            firstToggleRead + remainingToggleOutput.Length,
            "runtime limiter bypass preserves bounded queued audio");

        var repeatingLimiter = new SamplePeakLimiter(
            new RepeatingSampleProvider(sampleRate, channels: 2, 0.2f));
        var allocationBuffer = new float[2048];
        _ = repeatingLimiter.Read(
            allocationBuffer,
            0,
            allocationBuffer.Length);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 50; index++)
        {
            _ = repeatingLimiter.Read(
                allocationBuffer,
                0,
                allocationBuffer.Length);
        }

        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        AssertEqual(
            0L,
            allocatedAfter - allocatedBefore,
            "limiter steady-state Read allocation");
    }

    private static async Task RunVersionFiveAndSettingsMigrationTestsAsync(
        string root)
    {
        var libraryRoot = Path.Combine(root, "library");
        var soundsPath = Path.Combine(libraryRoot, "Sounds");
        Directory.CreateDirectory(soundsPath);
        var soundId = Guid.NewGuid();
        var managedFileName = $"{soundId:N}.wav";
        var managedPath = Path.Combine(soundsPath, managedFileName);
        WriteEditingWave(managedPath, channels: 1);
        var hash = await GetFileHashAsync(managedPath);
        await WriteJsonAsync(
            Path.Combine(libraryRoot, "library.json"),
            new
            {
                SchemaVersion = 5,
                Categories = Array.Empty<object>(),
                Sounds = new[]
                {
                    new
                    {
                        Id = soundId,
                        DisplayName = "Schema five",
                        ManagedFileName = managedFileName,
                        OriginalFileName = "source.wav",
                        FileType = "WAV",
                        Duration = TimeSpan.FromSeconds(1),
                        ImportedAtUtc = DateTimeOffset.UtcNow,
                        SortOrder = 0,
                        ContentHash = hash,
                        Container = AudioContainerType.Wav,
                        Codec = AudioCodecType.Pcm,
                        OriginalExtension = ".wav"
                    }
                }
            });
        await using (var store = new SoundLibraryStore(libraryRoot))
        {
            var sound = (await store.LoadAsync()).Sounds.Single();
            AssertTrue(
                !sound.NormalizeLoudness,
                "schema v5 defaults normalization disabled");
            AssertEqual(soundId, sound.Id, "schema v5 ID preserved");
            AssertEqual(hash, sound.ContentHash, "schema v5 hash preserved");
        }
        using (var migratedJson = JsonDocument.Parse(
                   await File.ReadAllTextAsync(
                       Path.Combine(libraryRoot, "library.json"))))
        {
            AssertEqual(
                6,
                migratedJson.RootElement
                    .GetProperty("schemaVersion")
                    .GetInt32(),
                "schema v5 migrates to v6");
        }

        var invalidRoot = Path.Combine(root, "invalid-normalization");
        var invalidSoundsPath = Path.Combine(invalidRoot, "Sounds");
        Directory.CreateDirectory(invalidSoundsPath);
        File.Copy(
            managedPath,
            Path.Combine(invalidSoundsPath, managedFileName));
        await WriteJsonAsync(
            Path.Combine(invalidRoot, "library.json"),
            new
            {
                SchemaVersion = 6,
                Categories = Array.Empty<object>(),
                Sounds = new[]
                {
                    new
                    {
                        Id = soundId,
                        DisplayName = "Invalid normalization",
                        ManagedFileName = managedFileName,
                        OriginalFileName = "source.wav",
                        FileType = "WAV",
                        Duration = TimeSpan.FromSeconds(1),
                        ImportedAtUtc = DateTimeOffset.UtcNow,
                        SortOrder = 0,
                        ContentHash = hash,
                        Container = AudioContainerType.Wav,
                        Codec = AudioCodecType.Pcm,
                        OriginalExtension = ".wav",
                        NormalizeLoudness = "invalid"
                    }
                }
            });
        await using (var store = new SoundLibraryStore(invalidRoot))
        {
            var loaded = await store.LoadAsync();
            AssertTrue(
                !loaded.Sounds.Single().NormalizeLoudness,
                "invalid normalization metadata falls back disabled");
            AssertTrue(
                loaded.Warnings.Any(
                    warning => warning.Contains(
                        "normalization",
                        StringComparison.OrdinalIgnoreCase)),
                "invalid normalization warning");
        }

        var settingsRoot = Path.Combine(root, "settings");
        await using (var settingsStore =
                     new ApplicationSettingsStore(settingsRoot))
        {
            var defaults = (await settingsStore.LoadAsync()).Settings;
            AssertEqual(
                -16d,
                defaults.NormalizationTargetLufs,
                "default normalization target");
            AssertTrue(defaults.SafetyLimiterEnabled, "default limiter on");
            AssertEqual(
                -1d,
                defaults.SafetyLimiterCeilingDbfs,
                "default limiter ceiling");
            await settingsStore.SaveAsync(
                defaults with
                {
                    NormalizationTargetLufs = -18.5d,
                    SafetyLimiterEnabled = false,
                    SafetyLimiterCeilingDbfs = -2.5d
                });
        }

        await using (var settingsStore =
                     new ApplicationSettingsStore(settingsRoot))
        {
            var persisted = (await settingsStore.LoadAsync()).Settings;
            AssertEqual(
                -18.5d,
                persisted.NormalizationTargetLufs,
                "target persists");
            AssertTrue(
                !persisted.SafetyLimiterEnabled,
                "limiter state persists");
            AssertEqual(
                -2.5d,
                persisted.SafetyLimiterCeilingDbfs,
                "ceiling persists");
        }

        await File.WriteAllTextAsync(
            Path.Combine(settingsRoot, "settings.json"),
            """
            {
              "normalizationTargetLufs": 99,
              "safetyLimiterEnabled": true,
              "safetyLimiterCeilingDbfs": -20
            }
            """);
        await using (var settingsStore =
                     new ApplicationSettingsStore(settingsRoot))
        {
            var invalid = await settingsStore.LoadAsync();
            AssertEqual(
                -16d,
                invalid.Settings.NormalizationTargetLufs,
                "invalid target fallback");
            AssertEqual(
                -1d,
                invalid.Settings.SafetyLimiterCeilingDbfs,
                "invalid ceiling fallback");
            AssertTrue(
                invalid.Warning is not null,
                "invalid settings warning");
        }

        Console.WriteLine(
            "PASS schema v5-to-v6 normalization migration and settings "
            + "defaults, persistence, and invalid fallback");
    }

    private static float[] CreateSineSamples(
        int sampleRate,
        int channels,
        double seconds,
        float amplitude)
    {
        var frames = checked((int)Math.Round(sampleRate * seconds));
        var samples = new float[frames * channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = amplitude * MathF.Sin(
                2f * MathF.PI * 440f * frame / sampleRate);
            for (var channel = 0; channel < channels; channel++)
            {
                samples[frame * channels + channel] = sample;
            }
        }

        return samples;
    }

    private static void WriteSineWaveFile(
        string path,
        int sampleRate,
        double seconds)
    {
        var samples = CreateSineSamples(
            sampleRate,
            channels: 1,
            seconds,
            amplitude: 0.2f);
        using var writer = new WaveFileWriter(
            path,
            new WaveFormat(sampleRate, 16, channels: 1));
        foreach (var sample in samples)
        {
            writer.WriteSample(sample);
        }
    }

    private static void AssertDecodedTrim(string path, string label)
    {
        using var decoded = AudioFileDecoderFactory.Default.Open(path);
        var durationMilliseconds = checked(
            (int)Math.Floor(decoded.Duration.TotalMilliseconds));
        AssertTrue(
            durationMilliseconds >= 100,
            $"{label} supports minimum clip duration");
        var start = durationMilliseconds >= 400
            ? durationMilliseconds / 4
            : 0;
        var end = durationMilliseconds >= 400
            ? durationMilliseconds * 3 / 4
            : durationMilliseconds;
        var settings = AudioClipSettings.Create(
            decoded.Duration,
            start,
            end == durationMilliseconds ? null : end,
            0,
            0);
        var provider = new AudioClipSampleProvider(
            decoded.SampleProvider,
            settings);
        var samples = ReadAllSamples(provider);
        var expectedFrames =
            AudioSamplePosition.TimeToFramePosition(
                settings.TrimEnd,
                decoded.SampleRate)
            - AudioSamplePosition.TimeToFramePosition(
                settings.TrimStart,
                decoded.SampleRate);
        AssertEqual(
            checked((int)(expectedFrames * decoded.ChannelCount)),
            samples.Length,
            $"{label} trimmed sample count");
        AssertEqual(
            0,
            provider.Read(new float[64], 0, 64),
            $"{label} trim remains at EOS");
    }

    private static float[] ReadAllSamples(ISampleProvider provider)
    {
        var result = new List<float>();
        var buffer = new float[4096];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            result.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return result.ToArray();
    }

    private static void AssertOneShotEndOfStream(
        string path,
        string message)
    {
        using var source = AudioFileDecoderFactory.Default.Open(path);
        var samples = ReadAllSamples(source);
        AssertTrue(samples > 0, $"{message} decoded samples");
        var buffer = new float[4096];
        AssertEqual(
            0,
            source.SampleProvider.Read(buffer, 0, buffer.Length),
            $"{message} remains at end of stream");
        AssertEqual(
            0,
            source.SampleProvider.Read(buffer, 0, buffer.Length),
            $"{message} never loops");
    }

    private static long ReadAllSamples(DecodedAudioSource source)
    {
        var buffer = new float[4096];
        long samples = 0;
        int read;
        while ((read = source.SampleProvider.Read(
                   buffer,
                   0,
                   buffer.Length)) > 0)
        {
            samples = checked(samples + read);
        }

        return samples;
    }

    private static void WriteTestOpus(
        string path,
        int channels,
        int frameCount = 4800)
    {
        const int sampleRate = 48000;
        var samples = new float[frameCount * channels];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sample = 0.15f * MathF.Sin(
                2 * MathF.PI * 440 * frame / sampleRate);
            for (var channel = 0; channel < channels; channel++)
            {
                samples[(frame * channels) + channel] = sample;
            }
        }

#pragma warning disable CS0618 // Generate a managed-only deterministic fixture.
        using var encoder = new OpusEncoder(
            sampleRate,
            channels,
            OpusApplication.OPUS_APPLICATION_AUDIO);
#pragma warning restore CS0618
        using var stream = File.Create(path);
        var writer = new OpusOggWriteStream(
            encoder,
            stream,
            new OpusTags { Comment = "Soundboard test fixture" },
            inputSampleRate: sampleRate,
            leaveOpen: true);
        writer.WriteSamples(samples, 0, samples.Length);
        writer.Finish();
    }

    private static void ReplaceFirstPacketSignature(
        byte[] ogg,
        ReadOnlySpan<byte> signature)
    {
        var bodyOffset = GetFirstPageBodyOffset(ogg);
        signature.CopyTo(ogg.AsSpan(bodyOffset, signature.Length));
        UpdateFirstPageCrc(ogg);
    }

    private static void SetOpusChannelCount(byte[] ogg, byte channels)
    {
        var bodyOffset = GetFirstPageBodyOffset(ogg);
        ogg[bodyOffset + 9] = channels;
        UpdateFirstPageCrc(ogg);
    }

    private static void SetOpusPreSkip(byte[] ogg, ushort preSkip)
    {
        var bodyOffset = GetFirstPageBodyOffset(ogg);
        BinaryPrimitives.WriteUInt16LittleEndian(
            ogg.AsSpan(bodyOffset + 10, 2),
            preSkip);
        UpdateFirstPageCrc(ogg);
    }

    private static int GetFirstPageBodyOffset(byte[] ogg)
    {
        return 27 + ogg[26];
    }

    private static void CorruptFirstPacketOnPage(
        byte[] ogg,
        int pageIndex)
    {
        var pageOffset = GetPageOffset(ogg, pageIndex);
        var segmentCount = ogg[pageOffset + 26];
        var bodyOffset = pageOffset + 27 + segmentCount;
        var firstPacketLength = 0;
        for (var index = 0; index < segmentCount; index++)
        {
            var segmentLength = ogg[pageOffset + 27 + index];
            firstPacketLength += segmentLength;
            if (segmentLength < byte.MaxValue)
            {
                break;
            }
        }

        ogg.AsSpan(bodyOffset, firstPacketLength).Fill(0xFF);
        UpdatePageCrc(
            ogg,
            pageOffset,
            GetPageLength(ogg, pageOffset));
    }

    private static void CorruptVorbisSetupPacket(byte[] ogg)
    {
        var pageOffset = GetPageOffset(ogg, pageIndex: 1);
        var segmentCount = ogg[pageOffset + 26];
        var bodyOffset = pageOffset + 27 + segmentCount;
        var firstPacketLength = 0;
        for (var index = 0; index < segmentCount; index++)
        {
            var segmentLength = ogg[pageOffset + 27 + index];
            firstPacketLength += segmentLength;
            if (segmentLength < byte.MaxValue)
            {
                break;
            }
        }

        ogg[bodyOffset + firstPacketLength] ^= 0x01;
        UpdatePageCrc(
            ogg,
            pageOffset,
            GetPageLength(ogg, pageOffset));
    }

    private static int GetPageOffset(byte[] ogg, int pageIndex)
    {
        var pageOffset = 0;
        for (var index = 0; index < pageIndex; index++)
        {
            pageOffset += GetPageLength(ogg, pageOffset);
        }

        return pageOffset;
    }

    private static int GetPageLength(byte[] ogg, int pageOffset)
    {
        var segmentCount = ogg[pageOffset + 26];
        var bodyLength = 0;
        for (var index = 0; index < segmentCount; index++)
        {
            bodyLength += ogg[pageOffset + 27 + index];
        }

        return 27 + segmentCount + bodyLength;
    }

    private static void UpdateFirstPageCrc(byte[] ogg)
    {
        var segmentCount = ogg[26];
        var bodyLength = 0;
        for (var index = 0; index < segmentCount; index++)
        {
            bodyLength += ogg[27 + index];
        }

        var pageLength = 27 + segmentCount + bodyLength;
        UpdatePageCrc(ogg, 0, pageLength);
    }

    private static void SetFinalOggGranule(byte[] ogg, long granule)
    {
        var pageOffset = 0;
        while (pageOffset < ogg.Length)
        {
            var segmentCount = ogg[pageOffset + 26];
            var bodyLength = 0;
            for (var index = 0; index < segmentCount; index++)
            {
                bodyLength += ogg[pageOffset + 27 + index];
            }

            var pageLength = 27 + segmentCount + bodyLength;
            if ((ogg[pageOffset + 5] & 0x04) != 0)
            {
                BinaryPrimitives.WriteInt64LittleEndian(
                    ogg.AsSpan(pageOffset + 6, 8),
                    granule);
                UpdatePageCrc(ogg, pageOffset, pageLength);
                return;
            }

            pageOffset += pageLength;
        }

        throw new InvalidOperationException(
            "The generated Ogg fixture has no end page.");
    }

    private static void UpdatePageCrc(
        byte[] ogg,
        int pageOffset,
        int pageLength)
    {
        ogg.AsSpan(pageOffset + 22, 4).Clear();
        uint crc = 0;
        for (var index = pageOffset;
             index < pageOffset + pageLength;
             index++)
        {
            crc ^= (uint)ogg[index] << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000) != 0
                    ? (crc << 1) ^ 0x04C11DB7
                    : crc << 1;
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            ogg.AsSpan(pageOffset + 22, 4),
            crc);
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

    private static async Task RunFormatMetadataFallbackAndFutureSchemaTestAsync(
        string root)
    {
        var fallbackRoot = Path.Combine(root, "fallback");
        var fallbackSounds = Path.Combine(fallbackRoot, "Sounds");
        Directory.CreateDirectory(fallbackSounds);
        var soundId = Guid.NewGuid();
        var managedName = $"{soundId:N}.ogg";
        await File.WriteAllBytesAsync(
            Path.Combine(fallbackSounds, managedName),
            [0x00]);
        await WriteJsonAsync(
            Path.Combine(fallbackRoot, "library.json"),
            new
            {
                SchemaVersion = 4,
                Categories = Array.Empty<object>(),
                Sounds = new[]
                {
                    new
                    {
                        Id = soundId,
                        DisplayName = "Format fallback",
                        ManagedFileName = managedName,
                        OriginalFileName = "format-fallback.ogg",
                        FileType = "OGG · Opus",
                        Duration = TimeSpan.FromSeconds(1),
                        ImportedAtUtc = DateTimeOffset.UtcNow,
                        SortOrder = 0,
                        ContentHash = "ABC123",
                        Container = "Video",
                        Codec = "Unknown",
                        OriginalExtension = ".bad"
                    }
                }
            });

        await using (var store = new SoundLibraryStore(fallbackRoot))
        {
            var loaded = await store.LoadAsync();
            AssertEqual(
                1,
                loaded.Sounds.Count,
                "invalid format metadata keeps sound");
            AssertEqual(
                AudioCodecType.Opus,
                loaded.Sounds[0].Codec,
                "invalid format metadata inferred from legacy label");
            AssertTrue(
                loaded.Warnings.Any(
                    warning => warning.Contains(
                        "invalid detected-format metadata",
                        StringComparison.OrdinalIgnoreCase)),
                "invalid format warning");
        }

        var futureRoot = Path.Combine(root, "future");
        var futureSounds = Path.Combine(futureRoot, "Sounds");
        Directory.CreateDirectory(futureSounds);
        var futureId = Guid.NewGuid();
        var futureManagedName = $"{futureId:N}.wav";
        await File.WriteAllBytesAsync(
            Path.Combine(futureSounds, futureManagedName),
            [0x00]);
        var futureLibraryPath = Path.Combine(futureRoot, "library.json");
        await WriteJsonAsync(
            futureLibraryPath,
            new
            {
                SchemaVersion = 99,
                FutureField = "preserve-me",
                Categories = Array.Empty<object>(),
                Sounds = new[]
                {
                    new
                    {
                        Id = futureId,
                        DisplayName = "Future",
                        ManagedFileName = futureManagedName,
                        OriginalFileName = "future.wav",
                        FileType = "WAV",
                        Duration = TimeSpan.FromSeconds(1),
                        ImportedAtUtc = DateTimeOffset.UtcNow,
                        SortOrder = 0,
                        ContentHash = "DEF456",
                        Container = "Wav",
                        Codec = "Pcm",
                        OriginalExtension = ".wav"
                    }
                }
            });
        var futureJsonBefore = await File.ReadAllTextAsync(futureLibraryPath);
        await using (var store = new SoundLibraryStore(futureRoot))
        {
            var loaded = await store.LoadAsync();
            AssertEqual(1, loaded.Sounds.Count, "future schema loads known data");
            AssertTrue(
                loaded.Warnings.Any(
                    warning => warning.Contains(
                        "newer",
                        StringComparison.OrdinalIgnoreCase)),
                "future schema warning");
            await AssertThrowsAsync<InvalidOperationException>(
                () => store.RenameAsync(futureId, "Must not save"),
                "future schema mutations are read-only");
        }

        AssertEqual(
            futureJsonBefore,
            await File.ReadAllTextAsync(futureLibraryPath),
            "future schema is not downgraded");
        Console.WriteLine(
            "PASS invalid format fallback and future schema preservation");
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

    private static async Task<string> GetFileHashAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream));
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

    private static void WriteEditingWave(string path, short channels)
    {
        const int sampleRate = 8000;
        const short bitsPerSample = 16;
        const int frameCount = sampleRate;
        var dataLength =
            frameCount * channels * bitsPerSample / 8;

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
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sample = (short)(
                short.MaxValue
                * 0.35
                * Math.Sin(2 * Math.PI * 440 * frame / sampleRate));
            for (var channel = 0; channel < channels; channel++)
            {
                writer.Write(sample);
            }
        }
    }

    private static void AssertThrows<TException>(
        Action action,
        string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed ({message}): expected "
            + $"{typeof(TException).Name}.");
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

    private sealed class TestSampleProvider : ISampleProvider
    {
        private readonly float[] samples;
        private int position;

        public TestSampleProvider(
            float[] samples,
            int sampleRate,
            int channels)
        {
            this.samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                sampleRate,
                channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, samples.Length - position);
            if (available <= 0)
            {
                return 0;
            }

            Array.Copy(samples, position, buffer, offset, available);
            position += available;
            return available;
        }
    }

    private sealed class RepeatingSampleProvider : ISampleProvider
    {
        private readonly float sample;

        public RepeatingSampleProvider(
            int sampleRate,
            int channels,
            float sample)
        {
            this.sample = sample;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                sampleRate,
                channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, sample, offset, count);
            return count;
        }
    }
}
