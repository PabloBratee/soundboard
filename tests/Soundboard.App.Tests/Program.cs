using System.Buffers.Binary;
using System.Text.Json;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using Soundboard.App.Hotkeys;
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
            $"Soundboard-Milestone7-Tests-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(testRoot);
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
            await RunOptionalLocalFormatTestsAsync(
                Path.Combine(testRoot, "local-formats"));
            Console.WriteLine("All Milestone 7 tests passed.");
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
                4,
                migratedJson.RootElement
                    .GetProperty("schemaVersion")
                    .GetInt32(),
                "schema persisted as v4");
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
            + "schema v4 format persistence, and personalization");
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

    private static void WriteTestOpus(string path, int channels)
    {
        const int sampleRate = 48000;
        const int frameCount = 4800;
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
}
