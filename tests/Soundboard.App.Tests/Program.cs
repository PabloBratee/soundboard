using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
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
            $"Soundboard-Tests-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(testRoot);
            RunSingleInstanceTests();
            RunHotkeyGestureTests();
            RunEndpointSelectionTests();
            await RunAutomaticAudioServiceLifecycleTestsAsync();
            await RunMigrationAndOrganizationTestsAsync(
                Path.Combine(testRoot, "organization"));
            await RunEmptyLibraryLifecycleTestsAsync(
                Path.Combine(testRoot, "empty-library"));
            await RunImportAndSearchTestsAsync(
                Path.Combine(testRoot, "import"));
            await RunOrganizationWorkflowTestsAsync(
                Path.Combine(testRoot, "organization-workflow"));
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
            RunGainAndBoundaryTests();
            await RunVersionOneSettingsAndVolumeMigrationTestsAsync(
                Path.Combine(testRoot, "settings-volume-migration"));
            await RunOptionalLocalFormatTestsAsync(
                Path.Combine(testRoot, "local-formats"));
            Console.WriteLine("All Soundboard tests passed.");
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

    private static void RunHotkeyGestureTests()
    {
        AssertTrue(
            HotkeyGesture.TryCreate(
                0x70,
                HotkeyModifiers.None,
                out var f1,
                out var f1Error),
            $"F1 without modifiers is valid: {f1Error}");
        AssertEqual("F1", f1!.DisplayText, "F1 display text");
        AssertEqual(
            0x4000u,
            GlobalHotkeyService.GetNativeModifiers(f1.Modifiers),
            "F1 registration uses MOD_NOREPEAT without key modifiers");
        var registrationConflictError =
            GlobalHotkeyService.BuildRegistrationError(f1, 1409);
        AssertTrue(
            registrationConflictError.StartsWith(
                "Windows could not register F1.",
                StringComparison.Ordinal)
            && registrationConflictError.Contains(
                "reserved or owned by another application",
                StringComparison.Ordinal),
            "a Windows registration conflict has a clear error message");

        AssertTrue(
            HotkeyGesture.TryCreate(
                0x70,
                HotkeyModifiers.Shift,
                out var shiftF1,
                out var shiftF1Error),
            $"Shift + F1 remains valid: {shiftF1Error}");
        AssertEqual(
            "Shift + F1",
            shiftF1!.DisplayText,
            "Shift + F1 display text");
        AssertEqual(
            0x4004u,
            GlobalHotkeyService.GetNativeModifiers(shiftF1.Modifiers),
            "Shift + F1 registration modifiers");

        foreach (var (virtualKey, expectedDisplay) in new[]
                 {
                     (0x41u, "A"),
                     (0x31u, "1"),
                     (0x61u, "Num 1")
                 })
        {
            AssertTrue(
                HotkeyGesture.TryCreate(
                    virtualKey,
                    HotkeyModifiers.None,
                    out var singleKey,
                    out var singleKeyError),
                $"{expectedDisplay} without modifiers is valid: "
                    + singleKeyError);
            AssertEqual(
                expectedDisplay,
                singleKey!.DisplayText,
                $"{expectedDisplay} display text");
        }

        AssertTrue(
            !HotkeyGesture.TryCreate(
                0x11,
                HotkeyModifiers.None,
                out _,
                out _),
            "a modifier-only Control input is rejected");

        var existingCombination = new HotkeyGesture(
            0x70,
            HotkeyModifiers.Control | HotkeyModifiers.Shift,
            "Ctrl + Shift + F1");
        AssertTrue(
            HotkeyGesture.TryNormalize(
                existingCombination,
                out var normalizedExistingCombination,
                out var normalizationError),
            $"existing modifier combination remains compatible: "
                + normalizationError);
        AssertEqual(
            existingCombination,
            normalizedExistingCombination,
            "existing modifier combination normalization");

        Console.WriteLine(
            "PASS optional hotkey modifiers, display text, validation, "
            + "registration flags, and existing combinations");
    }

    private static void RunEndpointSelectionTests()
    {
        var defaultMicrophone = new AudioEndpoint(
            "default-mic",
            "Default microphone",
            AudioDeviceDirection.Capture,
            AudioEndpointState.Active,
            IsDefault: true,
            IsLikelyVbCable: false);
        var pinnedMicrophone = new AudioEndpoint(
            "pinned-mic",
            "Pinned microphone",
            AudioDeviceDirection.Capture,
            AudioEndpointState.Active,
            IsDefault: false,
            IsLikelyVbCable: false);
        var virtualMicrophone = new AudioEndpoint(
            "cable-capture",
            "CABLE Output (VB-Audio Virtual Cable)",
            AudioDeviceDirection.Capture,
            AudioEndpointState.Active,
            IsDefault: false,
            IsLikelyVbCable: true);
        var standardVirtualRender = new AudioEndpoint(
            "standard-cable-render",
            "Renamed playback endpoint",
            AudioDeviceDirection.Render,
            AudioEndpointState.Active,
            IsDefault: false,
            IsLikelyVbCable: true,
            InterfaceFriendlyName: "VB-Audio Virtual Cable",
            EndpointDescription: "CABLE Input");
        var alternateVirtualRender = new AudioEndpoint(
            "alternate-cable-render",
            "CABLE In 16ch (VB-Audio Virtual Cable)",
            AudioDeviceDirection.Render,
            AudioEndpointState.Active,
            IsDefault: false,
            IsLikelyVbCable: true,
            InterfaceFriendlyName: "VB-Audio Virtual Cable",
            EndpointDescription: "CABLE In 16ch");

        var physical = AudioEndpointSelectionPolicy.PhysicalMicrophones(
            [defaultMicrophone, pinnedMicrophone, virtualMicrophone]);
        AssertSequence(
            [defaultMicrophone, pinnedMicrophone],
            physical,
            "virtual output is excluded from physical microphone choices");
        AssertEqual(
            defaultMicrophone,
            AudioEndpointSelectionPolicy.SelectMicrophone(
                physical,
                useWindowsDefault: true,
                pinnedEndpointId: "pinned-mic"),
            "Windows-default mode follows the communications default");
        AssertEqual(
            pinnedMicrophone,
            AudioEndpointSelectionPolicy.SelectMicrophone(
                physical,
                useWindowsDefault: false,
                pinnedEndpointId: "pinned-mic"),
            "pinned endpoint is remembered");
        AssertEqual(
            defaultMicrophone,
            AudioEndpointSelectionPolicy.SelectMicrophone(
                [defaultMicrophone],
                useWindowsDefault: false,
                pinnedEndpointId: "pinned-mic"),
            "missing pinned endpoint falls back to current default");
        AssertEqual(
            pinnedMicrophone,
            AudioEndpointSelectionPolicy.SelectMicrophone(
                physical,
                useWindowsDefault: false,
                pinnedEndpointId: "pinned-mic"),
            "returned pinned endpoint is selected again");

        var changedDefault = new[]
        {
            defaultMicrophone with { IsDefault = false },
            pinnedMicrophone with { IsDefault = true }
        };
        AssertEqual(
            pinnedMicrophone with { IsDefault = true },
            AudioEndpointSelectionPolicy.SelectMicrophone(
                changedDefault,
                useWindowsDefault: true,
                pinnedEndpointId: null),
            "default-device change is followed");
        AssertTrue(
            AudioDeviceService.IsLikelyVbCableDevice(
                "Localized endpoint name",
                "VB-Audio Virtual Cable",
                "Localized endpoint description",
                @"{2}.\\?\root#media#0000#...\vbaudiovacwdm2022_out1"),
            "driver interface metadata identifies a renamed VB-CABLE endpoint");
        AssertTrue(
            !AudioDeviceService.IsLikelyVbCableDevice(
                "Cable microphone (Acme USB Audio)",
                "Acme USB Audio",
                "Microphone",
                @"{2}.\\?\usb#vid_0001&pid_0002"),
            "an unrelated physical microphone containing cable is retained");
        AssertEqual(
            standardVirtualRender,
            AudioEndpointSelectionPolicy.SelectVirtualOutput(
                [alternateVirtualRender, standardVirtualRender],
                configuredEndpointId: null),
            "standard cable render is selected from endpoint metadata");
        AssertEqual(
            alternateVirtualRender,
            AudioEndpointSelectionPolicy.SelectVirtualOutput(
                [standardVirtualRender, alternateVirtualRender],
                "alternate-cable-render"),
            "saved virtual endpoint ID remains authoritative");
        AssertEqual(
            null,
            AudioEndpointSelectionPolicy.SelectVirtualOutput(
                [standardVirtualRender, alternateVirtualRender],
                "missing-saved-render"),
            "a missing saved virtual endpoint does not silently fall back");
        AssertEqual(
            "pinned-mic",
            AudioEndpointSelectionPolicy.UpdateConfiguredEndpointId(
                "pinned-mic",
                defaultMicrophone,
                userInitiated: false),
            "temporary microphone fallback does not overwrite the pinned ID");
        AssertEqual(
            "missing-saved-render",
            AudioEndpointSelectionPolicy.UpdateConfiguredEndpointId(
                "missing-saved-render",
                selectedEndpoint: null,
                userInitiated: false),
            "missing virtual output does not erase its configured ID");
        AssertEqual(
            "default-mic",
            AudioEndpointSelectionPolicy.UpdateConfiguredEndpointId(
                "pinned-mic",
                defaultMicrophone,
                userInitiated: true),
            "an explicit microphone choice replaces the pinned ID");
        AssertEqual(
            "alternate-cable-render",
            AudioEndpointSelectionPolicy.UpdateConfiguredEndpointId(
                "missing-saved-render",
                alternateVirtualRender,
                userInitiated: true),
            "an explicit virtual-output choice replaces the missing ID");

        Console.WriteLine(
            "PASS metadata-backed physical/virtual separation, pinned IDs, "
            + "default mode, device fallback/restoration, renamed/duplicate "
            + "VB-CABLE endpoints, and missing-output recovery");
    }

    private static async Task RunAutomaticAudioServiceLifecycleTestsAsync()
    {
        var state = AudioEngineState.Stopped;
        var startCount = 0;
        var stopCount = 0;
        var lifecycle = new AudioServiceLifecycle(
            () => state,
            (microphoneId, renderId, monitor) =>
            {
                AssertEqual("physical", microphoneId, "startup microphone endpoint");
                AssertEqual("cable", renderId, "startup virtual endpoint");
                startCount++;
                state = AudioEngineState.Running;
            },
            () =>
            {
                stopCount++;
                state = AudioEngineState.Stopped;
            });

        await lifecycle.ConnectAsync(
            "physical",
            "cable",
            new AudioMonitorConfiguration(false, null));
        AssertEqual(1, startCount, "audio service starts automatically when connected");
        AssertEqual(AudioEngineState.Running, state, "audio service remains running");

        await lifecycle.ConnectAsync(
            "physical",
            "cable",
            new AudioMonitorConfiguration(false, null));
        AssertEqual(1, stopCount, "reconnect releases the previous session first");
        AssertEqual(2, startCount, "reconnect creates one replacement session");

        await lifecycle.StopAsync();
        AssertEqual(2, stopCount, "shutdown releases the active audio session");
        AssertEqual(AudioEngineState.Stopped, state, "shutdown leaves audio stopped");
        await lifecycle.StopAsync();
        AssertEqual(2, stopCount, "repeated cleanup does not duplicate stop calls");

        Console.WriteLine(
            "PASS automatic audio-service startup, single-session reconnect, and idempotent shutdown cleanup");
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
                7,
                migratedJson.RootElement
                    .GetProperty("schemaVersion")
                    .GetInt32(),
                "schema persisted as v7");
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
                SoundTileAccent.Default,
                100d));
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
                SoundTileAccent.Default,
                100d));
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
                SoundTileAccent.Purple,
                100d));
        await store.UpdateSoundAsync(
            soundC,
            new SoundMetadataUpdate(
                "Charlie",
                categoryOne.Id,
                false,
                SoundTileAccent.Teal,
                100d));

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
                SoundTileAccent.Red,
                100d));
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
                SoundTileAccent.Green,
                100d));
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

    /// <summary>
    /// Covers the organization workflow end to end at the layer the UI calls:
    /// dragging one sound onto a category, bulk-moving a keyboard selection,
    /// undoing, importing into a destination, and category management.
    /// </summary>
    private static async Task RunOrganizationWorkflowTestsAsync(string root)
    {
        var sources = Path.Combine(root, "Sources");
        Directory.CreateDirectory(sources);
        var sourcePaths = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            var sourcePath = Path.Combine(sources, $"clip-{index}.wav");
            WriteDistinctTestWave(sourcePath, index + 1);
            sourcePaths.Add(sourcePath);
        }

        await using var store = new SoundLibraryStore(root);
        _ = await store.LoadAsync();
        var gaming = await store.CreateCategoryAsync("Gaming");
        var songs = await store.CreateCategoryAsync("Songs");

        // ---- Importing into a chosen destination --------------------------
        var gamingImport = await store.ImportAsync(
            sourcePaths.Take(3),
            gaming.Id);
        AssertEqual(
            3,
            gamingImport.Imported.Count,
            "import into the active category");
        AssertTrue(
            gamingImport.Imported.All(
                sound => sound.CategoryId == gaming.Id),
            "imported sounds land in the active category");

        // Dropping files onto a sidebar category uses the same import path,
        // including validation, hashing, and duplicate detection.
        var songsImport = await store.ImportAsync(
            sourcePaths.Skip(3),
            songs.Id);
        AssertEqual(
            2,
            songsImport.Imported.Count,
            "import onto a sidebar category");
        AssertTrue(
            songsImport.Imported.All(sound => sound.CategoryId == songs.Id),
            "files dropped on a category land in that category");
        var duplicateDrop = await store.ImportAsync(
            sourcePaths.Take(1),
            songs.Id);
        AssertEqual(
            0,
            duplicateDrop.Imported.Count,
            "duplicate detection still applies to a category drop");
        AssertEqual(
            1,
            duplicateDrop.Duplicates.Count,
            "a duplicate dropped on a category is reported");

        var alpha = gamingImport.Imported[0].Id;
        var bravo = gamingImport.Imported[1].Id;
        var charlie = gamingImport.Imported[2].Id;
        var delta = songsImport.Imported[0].Id;
        var echo = songsImport.Imported[1].Id;

        var hotkey = new HotkeyGesture(
            0x70,
            HotkeyModifiers.None,
            "F1");
        await store.UpdateHotkeyAsync(alpha, hotkey);
        string? duplicateHotkeyError = null;
        try
        {
            await store.UpdateHotkeyAsync(bravo, hotkey);
        }
        catch (InvalidOperationException exception)
        {
            duplicateHotkeyError = exception.Message;
        }

        AssertEqual(
            "F1 is already assigned to another sound.",
            duplicateHotkeyError,
            "duplicate F1 hotkey assignment error");
        await store.UpdateSoundAsync(
            alpha,
            new SoundMetadataUpdate(
                "Alpha",
                gaming.Id,
                true,
                SoundTileAccent.Teal,
                63d));
        await store.UpdateClipSettingsAsync(
            alpha,
            new SoundClipMetadataUpdate(10, 900, 5, 7));

        var orderBeforeMove = (await store.LoadAsync()).Sounds
            .Select(sound => sound.Id)
            .ToArray();
        AssertSequence(
            [alpha, bravo, charlie, delta, echo],
            orderBeforeMove,
            "imports keep their arrival order");

        // ---- Dragging one sound onto a category ---------------------------
        var singleMove = await store.MoveToCategoryAsync([alpha], songs.Id);
        AssertEqual(1, singleMove.MovedCount, "single drag move count");
        var movedAlpha = singleMove.Sounds.Single(sound => sound.Id == alpha);
        AssertEqual(
            songs.Id,
            movedAlpha.CategoryId,
            "a dragged sound lands in the drop target");
        AssertEqual(hotkey, movedAlpha.Hotkey, "a move preserves the hotkey");
        AssertTrue(movedAlpha.IsFavorite, "a move preserves the favorite flag");
        AssertEqual(
            SoundTileAccent.Teal,
            movedAlpha.TileAccent,
            "a move preserves the tile accent");
        AssertEqual(
            63d,
            movedAlpha.VolumePercent,
            "a move preserves the per-sound volume");
        AssertEqual(
            10,
            movedAlpha.TrimStartMilliseconds,
            "a move preserves the trim start");
        AssertEqual(
            900,
            movedAlpha.TrimEndMilliseconds,
            "a move preserves the trim end");
        AssertEqual(
            5,
            movedAlpha.FadeInMilliseconds,
            "a move preserves the fade in");
        AssertEqual(
            7,
            movedAlpha.FadeOutMilliseconds,
            "a move preserves the fade out");
        AssertEqual(
            "Alpha",
            movedAlpha.DisplayName,
            "a move preserves the display name");
        AssertSequence(
            [bravo, charlie, delta, echo, alpha],
            singleMove.Sounds.Select(sound => sound.Id),
            "a moved sound is appended to its destination category");
        AssertSequence(
            [0, 1, 2, 3, 4],
            singleMove.Sounds.Select(sound => sound.SortOrder),
            "a move renumbers the library order deterministically");

        await using (var reopened = new SoundLibraryStore(root))
        {
            var persisted = await reopened.LoadAsync();
            AssertEqual(
                songs.Id,
                persisted.Sounds.Single(sound => sound.Id == alpha)
                    .CategoryId,
                "a quick move persists without a separate save step");
            AssertSequence(
                [bravo, charlie, delta, echo, alpha],
                persisted.Sounds.Select(sound => sound.Id),
                "the order after a move persists");
        }

        // ---- Counts, filtering, and search stay correct -------------------
        var gamingView = new LibraryViewItem(
            SoundLibraryViewKind.Category,
            "Gaming",
            gaming.Id);
        var songsView = new LibraryViewItem(
            SoundLibraryViewKind.Category,
            "Songs",
            songs.Id);
        var favoritesView = new LibraryViewItem(
            SoundLibraryViewKind.Favorites,
            "Favorites");
        var uncategorizedView = new LibraryViewItem(
            SoundLibraryViewKind.Uncategorized,
            "Uncategorized");
        var views = new[]
        {
            gamingView,
            songsView,
            favoritesView,
            uncategorizedView
        };
        void ApplyCounts(IReadOnlyList<SoundLibraryEntry> sounds)
        {
            foreach (var view in views)
            {
                view.SoundCount = sounds.Count(
                    sound => SoundLibraryFilter.MatchesView(sound, view));
            }
        }

        ApplyCounts(singleMove.Sounds);
        AssertEqual(
            2,
            gamingView.SoundCount,
            "the source category count drops immediately");
        AssertEqual(
            3,
            songsView.SoundCount,
            "the destination category count rises immediately");
        AssertEqual(
            "2",
            gamingView.SoundCountText,
            "the sidebar count text follows the count");
        AssertEqual(
            1,
            favoritesView.SoundCount,
            "Favorites is unaffected by a category move");
        AssertTrue(
            !SoundLibraryFilter.MatchesView(movedAlpha, gamingView),
            "a moved sound leaves the view it was displayed in");
        AssertTrue(
            SoundLibraryFilter.MatchesView(movedAlpha, songsView),
            "a moved sound appears in its new view");
        AssertTrue(
            SoundLibraryFilter.MatchesSearch(movedAlpha, "Songs", "songs"),
            "search matches the new category name");
        AssertTrue(
            !SoundLibraryFilter.MatchesSearch(movedAlpha, "Songs", "gaming"),
            "search no longer matches the old category name");
        AssertTrue(
            gamingView.AcceptsSoundDrops
            && uncategorizedView.AcceptsSoundDrops
            && !favoritesView.AcceptsSoundDrops,
            "only views with an unambiguous category accept sound drops");
        AssertTrue(
            gamingView.AcceptsFileDrops
            && uncategorizedView.AcceptsFileDrops
            && !favoritesView.AcceptsFileDrops,
            "Favorites is not an import destination");

        // ---- Undo ---------------------------------------------------------
        var undone = await store.RestoreCategoryAssignmentsAsync(
            singleMove.Undo);
        AssertEqual(
            gaming.Id,
            undone.Single(sound => sound.Id == alpha).CategoryId,
            "undo restores the previous category");
        AssertSequence(
            orderBeforeMove,
            undone.Select(sound => sound.Id),
            "undo restores the previous order");

        // ---- Keyboard selection and the bulk command bar ------------------
        var selection = new LibrarySelectionState();
        var visibleSoundIds = undone.Select(sound => sound.Id).ToArray();
        AssertTrue(
            !selection.IsActive,
            "organization mode starts inactive");
        selection.ApplyClick(
            visibleSoundIds,
            bravo,
            extend: false,
            range: false);
        AssertTrue(
            selection.IsActive,
            "selecting a tile enters organization mode");
        selection.ApplyClick(
            visibleSoundIds,
            delta,
            extend: false,
            range: true);
        AssertSequence(
            [bravo, charlie, delta],
            selection.InVisualOrder(visibleSoundIds),
            "Shift+click selects a range in grid order");
        selection.ApplyClick(
            visibleSoundIds,
            charlie,
            extend: true,
            range: false);
        AssertSequence(
            [bravo, delta],
            selection.InVisualOrder(visibleSoundIds),
            "Ctrl+click removes a single sound from the selection");
        selection.SelectAll(visibleSoundIds);
        AssertEqual(
            5,
            selection.Count,
            "select all covers every visible sound");
        AssertEqual(
            "5 sounds selected",
            selection.SelectionCountText,
            "the command bar reports the selected count");
        selection.Clear();
        selection.ApplyClick(
            visibleSoundIds,
            bravo,
            extend: false,
            range: false);
        selection.ApplyClick(
            visibleSoundIds,
            delta,
            extend: true,
            range: false);
        var bulkSoundIds = selection.InVisualOrder(visibleSoundIds);
        AssertSequence(
            [bravo, delta],
            bulkSoundIds,
            "a bulk command acts on the selection in grid order");

        var jsonBeforeBulkMove = await File.ReadAllTextAsync(
            store.LibraryFilePath);
        var bulkMove = await store.MoveToCategoryAsync(bulkSoundIds, null);
        AssertEqual(2, bulkMove.MovedCount, "bulk move count");
        AssertTrue(
            bulkMove.Sounds
                .Where(sound => sound.Id == bravo || sound.Id == delta)
                .All(sound => sound.CategoryId is null),
            "a bulk move sends every selected sound to one destination");
        ApplyCounts(bulkMove.Sounds);
        AssertEqual(
            2,
            uncategorizedView.SoundCount,
            "Uncategorized reflects a bulk move immediately");

        var bulkUndone = await store.RestoreCategoryAssignmentsAsync(
            bulkMove.Undo);
        AssertEqual(
            gaming.Id,
            bulkUndone.Single(sound => sound.Id == bravo).CategoryId,
            "undo restores each sound's own previous category");
        AssertEqual(
            songs.Id,
            bulkUndone.Single(sound => sound.Id == delta).CategoryId,
            "undo restores a mixed selection correctly");
        AssertEqual(
            jsonBeforeBulkMove,
            await File.ReadAllTextAsync(store.LibraryFilePath),
            "undoing a bulk move returns the library file to its prior state");

        var noOpMove = await store.MoveToCategoryAsync([bravo], gaming.Id);
        AssertEqual(
            0,
            noOpMove.MovedCount,
            "moving a sound into the category it already has changes nothing");
        AssertTrue(
            !noOpMove.Undo.CanUndo,
            "a move that changed nothing offers no undo");

        var jsonBeforeFailure = await File.ReadAllTextAsync(
            store.LibraryFilePath);
        await AssertThrowsAsync<KeyNotFoundException>(
            () => store.MoveToCategoryAsync(
                [bravo, Guid.NewGuid()],
                songs.Id),
            "a bulk move naming a missing sound is rejected");
        AssertEqual(
            jsonBeforeFailure,
            await File.ReadAllTextAsync(store.LibraryFilePath),
            "a rejected bulk move writes nothing at all");

        // ---- Bulk favorite -------------------------------------------------
        var favorited = await store.SetFavoriteAsync([bravo, charlie], true);
        AssertTrue(
            favorited
                .Where(sound => sound.Id == bravo || sound.Id == charlie)
                .All(sound => sound.IsFavorite),
            "the command bar can favorite several sounds at once");
        ApplyCounts(favorited);
        AssertEqual(
            3,
            favoritesView.SoundCount,
            "Favorites reflects a bulk favorite immediately");
        var unfavorited = await store.SetFavoriteAsync(
            [bravo, charlie],
            false);
        AssertTrue(
            unfavorited
                .Where(sound => sound.Id == bravo || sound.Id == charlie)
                .All(sound => !sound.IsFavorite),
            "the command bar can clear favorites again");

        // ---- Category management -------------------------------------------
        var renamedCategory = await store.RenameCategoryAsync(
            songs.Id,
            "Music");
        AssertEqual(
            "Music",
            renamedCategory.DisplayName,
            "the inline field renames a category");
        await AssertThrowsAsync<InvalidOperationException>(
            () => store.RenameCategoryAsync(gaming.Id, "music"),
            "renaming to an existing name is rejected");
        await AssertThrowsAsync<InvalidOperationException>(
            () => store.CreateCategoryAsync("Favorites"),
            "built-in view names stay reserved");

        var managedFilesBeforeDelete = Directory
            .GetFiles(store.SoundsPath)
            .Order()
            .ToArray();
        var deletion = await store.DeleteCategoryAsync(songs.Id);
        AssertEqual(
            5,
            deletion.Sounds.Count,
            "deleting a category removes no sounds");
        AssertTrue(
            deletion.Sounds
                .Where(sound => sound.Id == delta || sound.Id == echo)
                .All(sound => sound.CategoryId is null),
            "sounds from a deleted category move to Uncategorized");
        AssertSequence(
            managedFilesBeforeDelete,
            Directory.GetFiles(store.SoundsPath).Order(),
            "deleting a category deletes no audio file");

        await using (var restarted = new SoundLibraryStore(root))
        {
            var persisted = await restarted.LoadAsync();
            AssertEqual(
                5,
                persisted.Sounds.Count,
                "the library survives a restart");
            AssertEqual(
                1,
                persisted.Categories.Count,
                "the remaining categories survive a restart");
            var persistedAlpha = persisted.Sounds.Single(
                sound => sound.Id == alpha);
            AssertEqual(
                gaming.Id,
                persistedAlpha.CategoryId,
                "a category assignment survives a restart");
            AssertEqual(
                hotkey,
                persistedAlpha.Hotkey,
                "a hotkey survives the organization workflow");
            AssertEqual(
                63d,
                persistedAlpha.VolumePercent,
                "a per-sound volume survives the organization workflow");
            AssertEqual(
                10,
                persistedAlpha.TrimStartMilliseconds,
                "a trim survives the organization workflow");
            AssertEqual(
                SoundTileAccent.Teal,
                persistedAlpha.TileAccent,
                "a tile accent survives the organization workflow");
        }

        var pruning = new LibrarySelectionState();
        pruning.SelectAll(visibleSoundIds);
        pruning.Retain([bravo, charlie]);
        AssertEqual(
            2,
            pruning.Count,
            "the selection drops sounds that left the library");
        pruning.Deactivate();
        AssertTrue(
            !pruning.IsActive && pruning.Count == 0,
            "leaving organization mode clears the selection");

        Console.WriteLine(
            "PASS category moves, bulk moves, undo, import destinations, "
            + "selection rules, counts, and category management");
    }

    /// <summary>
    /// A one-second silent WAV with a seeded marker sample so every fixture
    /// hashes differently and duplicate detection stays meaningful.
    /// </summary>
    private static void WriteDistinctTestWave(string path, int seed)
    {
        const int sampleRate = 8000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int sampleCount = sampleRate;
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
            writer.Write(index == 0 ? (short)seed : (short)0);
        }
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
                    SoundTileAccent.Purple,
                    100d));
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
            + "schema v7 format persistence, and personalization");
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
                7,
                persisted.RootElement.GetProperty("schemaVersion").GetInt32(),
                "v4 migrated to schema v7");
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
            var removeWarnings = await reloadedStore.RemoveAsync(soundId);
            AssertEqual(0, removeWarnings.Count, "waveform removal warnings");
            AssertTrue(!File.Exists(managedPath), "managed copy removed");
            AssertTrue(!File.Exists(cachePath), "waveform cache removed");
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

    private static void RunGainAndBoundaryTests()
    {
        AssertEqual(0f, AudioGain.FromPercent(0d), "zero percent is digital silence");
        AssertTrue(
            Math.Abs(AudioGain.FromPercent(50d) - 0.25f) < 0.000001f,
            "fifty percent uses the documented squared taper");
        AssertTrue(
            Math.Abs(AudioGain.FromPercent(25d) - 0.0625f) < 0.000001f,
            "twenty-five percent uses the documented squared taper");
        AssertTrue(
            Math.Abs(AudioGain.FromPercent(75d) - 0.5625f) < 0.000001f,
            "seventy-five percent uses the documented squared taper");
        AssertEqual(1f, AudioGain.FromPercent(100d), "one hundred percent is unity");
        var previousGain = -1f;
        for (var percent = 0; percent <= 100; percent++)
        {
            var gain = AudioGain.FromPercent(percent);
            AssertTrue(gain >= previousGain, "volume curve remains monotonic");
            AssertTrue(gain is >= 0f and <= 1f, "volume curve remains bounded by unity");
            previousGain = gain;
        }
        AssertTrue(
            Math.Abs(AudioGain.Combine(50d, 50d) - 0.0625f) < 0.000001f,
            "per-sound and master gain are each applied exactly once");
        AssertThrows<ArgumentOutOfRangeException>(
            () => AudioGain.FromPercent(-1d),
            "negative sound gain is rejected");
        using (var engine = new AudioMixEngine())
        {
            AssertThrows<ArgumentOutOfRangeException>(
                () => engine.SoundVolume = 1.01f,
                "sound master cannot exceed unity gain");
            AssertThrows<ArgumentOutOfRangeException>(
                () => engine.SoundVolume = float.NaN,
                "non-finite sound master gain is rejected");
        }

        var smoothGain = new SmoothGainSampleProvider(
            new TestSampleProvider(
                Enumerable.Repeat(1f, 20).ToArray(),
                1000,
                1),
            initialGain: 1f);
        AssertSequence(
            [1f],
            ReadSamples(smoothGain, 1),
            "initial gain is applied without an unintended fade-in");
        smoothGain.SetGain(0f);
        var rampDown = ReadSamples(smoothGain, 5);
        AssertTrue(
            rampDown.Zip(rampDown.Skip(1), (left, right) => left >= right).All(value => value),
            "live gain changes ramp monotonically without a discontinuous sample jump");
        AssertTrue(
            rampDown.Zip(rampDown.Skip(1), (left, right) => left - right)
                .All(step => step <= 0.200001f),
            "five-millisecond live gain ramp bounds adjacent-sample steps");
        AssertTrue(
            Math.Abs(rampDown[^1]) < 0.000001f,
            "live gain ramp reaches exact digital silence");
        smoothGain.SetGain(1f);
        var rampUp = ReadSamples(smoothGain, 5);
        AssertTrue(
            Math.Abs(rampUp[^1] - 1f) < 0.000001f,
            "live gain ramp reaches exact unity");

        var validSamples = new[] { -1f, -0.5f, 0f, 0.5f, 1f };
        var transparent = new SampleBoundarySanitizer(
            new TestSampleProvider(validSamples, 48000, 1));
        AssertSequence(
            validSamples,
            ReadAllSamples(transparent),
            "final boundary leaves valid samples unchanged");
        AssertEqual(0L, transparent.ClippedSampleCount, "valid samples are not clipped");

        var invalid = new SampleBoundarySanitizer(
            new TestSampleProvider(
                [-2f, -1f, float.NaN, float.PositiveInfinity, 0.25f, 1f, 2f],
                48000,
                1));
        AssertSequence(
            [-1f, -1f, 0f, 0f, 0.25f, 1f, 1f],
            ReadAllSamples(invalid),
            "final boundary only clips invalid values");
        AssertEqual(2L, invalid.ClippedSampleCount, "over-range sample count");
        AssertEqual(2L, invalid.NonFiniteSampleCount, "non-finite sample count");

        var sound = new VolumeSampleProvider(
            new TestSampleProvider([0.8f], 48000, 1))
        {
            Volume = AudioGain.Combine(50d, 100d)
        };
        AssertTrue(
            Math.Abs(ReadAllSamples(sound).Single() - 0.2f) < 0.000001f,
            "known PCM amplitude follows per-sound and master gain");

        var microphone = new TestSampleProvider([0.25f], 48000, 1);
        var mutedSound = new VolumeSampleProvider(
            new TestSampleProvider([0.75f], 48000, 1))
        {
            Volume = AudioGain.FromPercent(0d)
        };
        var mix = new MixingSampleProvider(
            new ISampleProvider[] { microphone, mutedSound });
        AssertTrue(
            Math.Abs(ReadAllSamples(mix).Single() - 0.25f) < 0.000001f,
            "zero sound master leaves microphone passthrough unchanged");

        var fixture = Enumerable.Range(0, 100)
            .Select(index => index == 0 ? 0.8f : 0.1f)
            .ToArray();
        var decoderFactory = new TestDecoderFactory(fixture, 1000, 1);
        var clipSettings = AudioClipSettings.FullDuration(
            TimeSpan.FromMilliseconds(100));
        using var firstSession = new SoundPlaybackSession(
            Guid.NewGuid(),
            1,
            "fixture.wav",
            clipSettings,
            decoderFactory,
            WaveFormat.CreateIeeeFloatWaveFormat(1000, 1),
            volumePercent: 50d,
            masterGain: AudioGain.FromPercent(100d),
            monitorGain: 1f);
        var partial = new float[10];
        AssertEqual(
            10,
            firstSession.VirtualBranch.Read(partial, 0, partial.Length),
            "first trigger begins playback");
        AssertTrue(
            Math.Abs(partial[0] - 0.2f) < 0.000001f,
            "broadcast branch applies the configured sound gain");
        AssertTrue(
            Math.Abs(
                firstSession.MonitorBranchGain
                - AudioGain.Combine(50d, 100d)) < 0.000001f,
            "preview/monitor and broadcast use the same sound gain before explicit monitor volume");

        using var restartedSession = new SoundPlaybackSession(
            firstSession.SoundId,
            2,
            "fixture.wav",
            clipSettings,
            decoderFactory,
            WaveFormat.CreateIeeeFloatWaveFormat(1000, 1),
            volumePercent: 50d,
            masterGain: AudioGain.FromPercent(100d),
            monitorGain: 1f);
        var restarted = ReadAllSamples(restartedSession.VirtualBranch);
        AssertEqual(100, restarted.Length, "re-triggered session plays exactly once");
        AssertTrue(
            Math.Abs(restarted[0] - 0.2f) < 0.000001f,
            "re-trigger starts again at the first sample");
        AssertEqual(
            0,
            restartedSession.VirtualBranch.Read(new float[8], 0, 8),
            "sound does not loop or replay after end-of-stream");

        var rapidSoundId = Guid.NewGuid();
        for (var trigger = 0; trigger < 25; trigger++)
        {
            var rapidSession = new SoundPlaybackSession(
                rapidSoundId,
                trigger + 3,
                "fixture.wav",
                clipSettings,
                decoderFactory,
                WaveFormat.CreateIeeeFloatWaveFormat(1000, 1),
                volumePercent: 100d,
                masterGain: 1f,
                monitorGain: 1f);
            AssertTrue(
                rapidSession.VirtualBranch.Read(new float[8], 0, 8) > 0,
                "rapid retrigger session starts at decodable audio");
            rapidSession.Dispose();
            AssertEqual(
                0,
                rapidSession.VirtualBranch.Read(new float[8], 0, 8),
                "replaced rapid retrigger session remains disposed");
        }

        using var concurrentSession = new SoundPlaybackSession(
            Guid.NewGuid(),
            3,
            "fixture.wav",
            clipSettings,
            decoderFactory,
            WaveFormat.CreateIeeeFloatWaveFormat(1000, 1),
            volumePercent: 100d,
            masterGain: AudioGain.FromPercent(100d),
            monitorGain: 1f);
        using var secondConcurrentSession = new SoundPlaybackSession(
            Guid.NewGuid(),
            4,
            "fixture.wav",
            clipSettings,
            decoderFactory,
            WaveFormat.CreateIeeeFloatWaveFormat(1000, 1),
            volumePercent: 100d,
            masterGain: AudioGain.FromPercent(100d),
            monitorGain: 1f);
        var concurrentMix = new MixingSampleProvider(
            new ISampleProvider[]
            {
                concurrentSession.VirtualBranch,
                secondConcurrentSession.VirtualBranch
            });
        var concurrentOutput = ReadAllSamples(concurrentMix);
        AssertEqual(100, concurrentOutput.Length, "simultaneous sounds share one bounded mix");
        AssertTrue(
            Math.Abs(concurrentOutput[0] - 1.6f) < 0.000001f,
            "different sounds mix concurrently before final clipping");

        Console.WriteLine(
            "PASS squared volume curve, exact silence/unity, bounded live ramps, "
            + "single-application gain, microphone independence, concurrent "
            + "one-shots, bounded rapid restart, no-loop EOS, and transparent "
            + "final-boundary clipping");
    }
    private static async Task RunVersionOneSettingsAndVolumeMigrationTestsAsync(
        string root)
    {
        var libraryRoot = Path.Combine(root, "library");
        var soundsPath = Path.Combine(libraryRoot, "Sounds");
        Directory.CreateDirectory(soundsPath);
        var soundId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var importedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        var hotkey = new HotkeyGesture(
            0x42,
            HotkeyModifiers.Control | HotkeyModifiers.Shift,
            "Ctrl + Shift + B");
        var managedFileName = $"{soundId:N}.wav";
        var managedPath = Path.Combine(soundsPath, managedFileName);
        WriteEditingWave(managedPath, channels: 1);
        var hash = await GetFileHashAsync(managedPath);
        await WriteJsonAsync(
            Path.Combine(libraryRoot, "library.json"),
            new
            {
                SchemaVersion = 6,
                Categories = new[]
                {
                    new SoundCategory(
                        categoryId,
                        "Legacy category",
                        0,
                        importedAt)
                },
                Sounds = new[]
                {
                    new
                    {
                        Id = soundId,
                        DisplayName = "Existing sound",
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
                        TileAccent = SoundTileAccent.Blue,
                        Container = AudioContainerType.Wav,
                        Codec = AudioCodecType.Pcm,
                        OriginalExtension = ".wav",
                        TrimStartMilliseconds = 100,
                        TrimEndMilliseconds = 800,
                        FadeInMilliseconds = 50,
                        FadeOutMilliseconds = 75,
                        NormalizeLoudness = true
                    }
                }
            });

        await using (var store = new SoundLibraryStore(libraryRoot))
        {
            var migrated = await store.LoadAsync();
            var sound = migrated.Sounds.Single();
            AssertEqual(soundId, sound.Id, "existing sound ID preserved");
            AssertEqual(hash, sound.ContentHash, "existing managed file preserved");
            AssertEqual(100d, sound.VolumePercent, "existing sound gets unity volume");
            AssertEqual(hotkey, sound.Hotkey, "existing hotkey is preserved");
            AssertEqual(categoryId, sound.CategoryId, "existing category assignment is preserved");
            AssertTrue(sound.IsFavorite, "existing favorite is preserved");
            AssertEqual(SoundTileAccent.Blue, sound.TileAccent, "existing tile accent is preserved");
            AssertEqual(0, sound.SortOrder, "existing page order is preserved");
            AssertEqual(importedAt, sound.ImportedAtUtc, "existing import timestamp is preserved");
            AssertEqual(100, sound.TrimStartMilliseconds, "existing trim start is preserved");
            AssertEqual(800, sound.TrimEndMilliseconds, "existing trim end is preserved");
            AssertEqual(50, sound.FadeInMilliseconds, "existing fade-in is preserved");
            AssertEqual(75, sound.FadeOutMilliseconds, "existing fade-out is preserved");
            AssertTrue(File.Exists(managedPath), "migration does not delete user audio");
            AssertTrue(
                migrated.Warnings.Any(
                    warning => warning.Contains(
                        "schema version 6",
                        StringComparison.OrdinalIgnoreCase)),
                "schema migration is reported");

        }

        var migratedLibraryHash = await GetFileHashAsync(
            Path.Combine(libraryRoot, "library.json"));
        var migratedLibraryWriteTime = File.GetLastWriteTimeUtc(
            Path.Combine(libraryRoot, "library.json"));
        var managedHashAfterMigration = await GetFileHashAsync(managedPath);
        await using (var store = new SoundLibraryStore(libraryRoot))
        {
            var reloaded = (await store.LoadAsync()).Sounds.Single();
            AssertEqual(100d, reloaded.VolumePercent, "second launch reads migrated volume");
        }
        AssertEqual(
            migratedLibraryHash,
            await GetFileHashAsync(Path.Combine(libraryRoot, "library.json")),
            "second launch does not rewrite migrated library metadata");
        AssertEqual(
            migratedLibraryWriteTime,
            File.GetLastWriteTimeUtc(Path.Combine(libraryRoot, "library.json")),
            "second launch preserves migrated library timestamp");
        AssertEqual(
            managedHashAfterMigration,
            await GetFileHashAsync(managedPath),
            "managed audio fingerprint is unchanged by migration and reload");

        await using (var store = new SoundLibraryStore(libraryRoot))
        {
            var sound = (await store.LoadAsync()).Sounds.Single();
            var updated = await store.UpdateSoundAsync(
                soundId,
                new SoundMetadataUpdate(
                    sound.DisplayName,
                    sound.CategoryId,
                    sound.IsFavorite,
                    sound.TileAccent,
                    37d));
            AssertEqual(37d, updated.VolumePercent, "per-sound volume updates");
        }

        await using (var store = new SoundLibraryStore(libraryRoot))
        {
            var reloaded = (await store.LoadAsync()).Sounds.Single();
            AssertEqual(37d, reloaded.VolumePercent, "per-sound volume persists");
        }

        using (var libraryJson = JsonDocument.Parse(
                   await File.ReadAllTextAsync(
                       Path.Combine(libraryRoot, "library.json"))))
        {
            AssertEqual(
                7,
                libraryJson.RootElement.GetProperty("schemaVersion").GetInt32(),
                "library migrates to schema v7");
            var soundJson = libraryJson.RootElement
                .GetProperty("sounds")[0];
            AssertTrue(
                !soundJson.TryGetProperty("normalizeLoudness", out _),
                "obsolete per-sound normalization is removed");
            AssertEqual(
                37d,
                soundJson.GetProperty("volumePercent").GetDouble(),
                "per-sound volume stored explicitly");
        }

        var settingsRoot = Path.Combine(root, "settings");
        Directory.CreateDirectory(settingsRoot);
        await File.WriteAllTextAsync(
            Path.Combine(settingsRoot, "settings.json"),
            """
            {
              "schemaVersion": 1,
              "microphoneEndpointId": "physical-endpoint-id",
              "virtualOutputEndpointId": "cable-render-id",
              "soundVolume": 0.6,
              "monitorVolume": 0.4,
              "microphoneVolume": 1.8,
              "microphoneMuted": true,
              "normalizationTargetLufs": -18.0,
              "safetyLimiterEnabled": false,
              "safetyLimiterCeilingDbfs": -3.0
            }
            """);

        ApplicationSettings migratedSettings;
        await using (var store = new ApplicationSettingsStore(settingsRoot))
        {
            var result = await store.LoadAsync();
            migratedSettings = result.Settings;
            AssertEqual(2, migratedSettings.SchemaVersion, "settings schema v2");
            AssertTrue(!migratedSettings.UseDefaultMicrophone, "saved mic remains pinned");
            AssertTrue(migratedSettings.SetupCompleted, "complete v1 routing is preserved");
            AssertEqual(
                "physical-endpoint-id",
                migratedSettings.MicrophoneEndpointId,
                "stable physical endpoint ID preserved");
            AssertEqual(
                "cable-render-id",
                migratedSettings.VirtualOutputEndpointId,
                "stable virtual endpoint ID preserved");
            AssertEqual(0.6d, migratedSettings.SoundVolume, "master volume preserved");
            AssertEqual(0.4d, migratedSettings.MonitorVolume, "monitor volume preserved");
            AssertTrue(
                result.Warning?.Contains(
                    "obsolete",
                    StringComparison.OrdinalIgnoreCase) == true,
                "obsolete settings migration is reported");
        }

        using (var settingsJson = JsonDocument.Parse(
                   await File.ReadAllTextAsync(
                       Path.Combine(settingsRoot, "settings.json"))))
        {
            AssertTrue(
                !settingsJson.RootElement.TryGetProperty("normalizationTargetLufs", out _)
                && !settingsJson.RootElement.TryGetProperty("safetyLimiterEnabled", out _)
                && !settingsJson.RootElement.TryGetProperty("safetyLimiterCeilingDbfs", out _)
                && !settingsJson.RootElement.TryGetProperty("microphoneVolume", out _)
                && !settingsJson.RootElement.TryGetProperty("microphoneMuted", out _),
                "obsolete automatic gain and safety settings are removed");
        }

        var migratedSettingsHash = await GetFileHashAsync(
            Path.Combine(settingsRoot, "settings.json"));
        var migratedSettingsWriteTime = File.GetLastWriteTimeUtc(
            Path.Combine(settingsRoot, "settings.json"));
        await using (var store = new ApplicationSettingsStore(settingsRoot))
        {
            var secondLoad = await store.LoadAsync();
            AssertEqual(
                migratedSettings,
                secondLoad.Settings,
                "second launch reads identical migrated settings");
        }
        AssertEqual(
            migratedSettingsHash,
            await GetFileHashAsync(Path.Combine(settingsRoot, "settings.json")),
            "second launch does not rewrite migrated settings");
        AssertEqual(
            migratedSettingsWriteTime,
            File.GetLastWriteTimeUtc(Path.Combine(settingsRoot, "settings.json")),
            "second launch preserves migrated settings timestamp");

        var defaultRoot = Path.Combine(root, "default-mode");
        Directory.CreateDirectory(defaultRoot);
        await File.WriteAllTextAsync(
            Path.Combine(defaultRoot, "settings.json"),
            """{"schemaVersion":1,"virtualOutputEndpointId":"cable-render-id"}""");
        await using (var store = new ApplicationSettingsStore(defaultRoot))
        {
            var settings = (await store.LoadAsync()).Settings;
            AssertTrue(settings.UseDefaultMicrophone, "missing pinned mic selects Windows default mode");
            AssertTrue(!settings.SetupCompleted, "incomplete legacy routing shows setup");
        }

        var failedMigrationRoot = Path.Combine(root, "failed-settings-migration");
        Directory.CreateDirectory(failedMigrationRoot);
        var failedMigrationPath = Path.Combine(
            failedMigrationRoot,
            "settings.json");
        var failedMigrationBytes =
            """{"schemaVersion":1,"microphoneEndpointId":"pinned"}""";
        await File.WriteAllTextAsync(failedMigrationPath, failedMigrationBytes);
        await using (var lockedSettings = new FileStream(
                         failedMigrationPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        await using (var store = new ApplicationSettingsStore(failedMigrationRoot))
        {
            var result = await store.LoadAsync();
            AssertTrue(
                result.Warning?.Contains(
                    "could not be saved",
                    StringComparison.OrdinalIgnoreCase) == true,
                "failed settings migration reports the unsaved state");
            AssertTrue(
                !result.Settings.UseDefaultMicrophone,
                "failed settings migration still provides safe in-memory settings");
        }
        AssertEqual(
            failedMigrationBytes,
            await File.ReadAllTextAsync(failedMigrationPath),
            "failed migration leaves the previous settings file intact");

        Console.WriteLine(
            "PASS v1.0 settings migration, default/pinned microphone modes, "
            + "schema-v7 per-sound volume, idempotent atomic migration, "
            + "obsolete processing removal, and user-data preservation");
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

    private static float[] ReadSamples(ISampleProvider provider, int count)
    {
        var buffer = new float[count];
        var read = provider.Read(buffer, 0, count);
        AssertEqual(count, read, "requested gain-test sample count");
        return buffer;
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

    private static async Task RunEmptyLibraryLifecycleTestsAsync(string root)
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "user-owned.wav");
        WriteTestWave(source);

        var resourceNames = typeof(SoundLibraryStore).Assembly
            .GetManifestResourceNames();
        var audioExtensions = new[]
        {
            ".mp3", ".wav", ".ogg", ".opus", ".flac", ".m4a", ".aac", ".wma"
        };
        AssertTrue(
            resourceNames.All(
                name => !audioExtensions.Any(
                    extension => name.EndsWith(
                        extension,
                        StringComparison.OrdinalIgnoreCase))),
            "application assembly contains no embedded audio resources");
        AssertTrue(
            resourceNames.All(
                name => !name.Contains(
                    "StarterLibrary",
                    StringComparison.OrdinalIgnoreCase)),
            "application assembly contains no starter-library resources");

        Guid soundId;
        string managedPath;
        string contentHash;
        var hotkey = new HotkeyGesture(
            0x47,
            HotkeyModifiers.Control | HotkeyModifiers.Shift,
            "Ctrl + Shift + G");
        await using (var store = new SoundLibraryStore(root))
        {
            var fresh = await store.LoadAsync();
            AssertEqual(0, fresh.Sounds.Count, "fresh library has zero sounds");
            AssertEqual(0, fresh.Categories.Count, "fresh library has zero categories");
            AssertEqual(
                0,
                Directory.GetFiles(store.SoundsPath).Length,
                "startup creates no managed audio");

            var category = await store.CreateCategoryAsync("Personal");
            var import = await store.ImportAsync([source], category.Id);
            AssertEqual(1, import.Imported.Count, "user-controlled WAV import succeeds");
            AssertEqual(0, import.Duplicates.Count, "first import is not a duplicate");
            var updated = await store.UpdateSoundAsync(
                import.Imported[0].Id,
                new SoundMetadataUpdate(
                    "User Sound",
                    category.Id,
                    true,
                    SoundTileAccent.Purple,
                    75d));
            updated = await store.UpdateHotkeyAsync(updated.Id, hotkey);
            soundId = updated.Id;
            managedPath = store.GetManagedFilePath(updated);
            contentHash = updated.ContentHash;
            AssertTrue(File.Exists(managedPath), "import creates a managed local copy");
        }

        await using (var restarted = new SoundLibraryStore(root))
        {
            var loaded = await restarted.LoadAsync();
            var preserved = loaded.Sounds.Single();
            AssertEqual(soundId, preserved.Id, "restart preserves imported sound ID");
            AssertEqual("User Sound", preserved.DisplayName, "restart preserves user name");
            AssertEqual(contentHash, preserved.ContentHash, "restart preserves content hash");
            AssertEqual(hotkey, preserved.Hotkey, "restart preserves user hotkey");
            AssertTrue(preserved.IsFavorite, "restart preserves favorite setting");
            AssertEqual(
                SoundTileAccent.Purple,
                preserved.TileAccent,
                "restart preserves tile settings");

            var duplicate = await restarted.ImportAsync([source]);
            AssertEqual(0, duplicate.Imported.Count, "duplicate import is rejected");
            AssertEqual(1, duplicate.Duplicates.Count, "duplicate is reported");

            var warnings = await restarted.RemoveAsync(soundId);
            AssertEqual(0, warnings.Count, "imported sound deletion succeeds");
            AssertTrue(!File.Exists(managedPath), "deletion removes the managed copy");
        }

        await using (var afterDeletion = new SoundLibraryStore(root))
        {
            var loaded = await afterDeletion.LoadAsync();
            AssertEqual(
                0,
                loaded.Sounds.Count,
                "deleted sound does not return on restart");
            AssertEqual(
                0,
                Directory.GetFiles(afterDeletion.SoundsPath).Length,
                "restart does not recreate deleted audio");
        }

        Console.WriteLine(
            "PASS empty startup, no embedded audio, user import, duplicate "
            + "protection, metadata persistence, and no restore after deletion");
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

    private sealed class TestDecoderFactory(
        float[] samples,
        int sampleRate,
        int channels) : IAudioFileDecoderFactory
    {
        public DecodedAudioSource Open(string filePath)
        {
            var duration = TimeSpan.FromSeconds(
                samples.Length / (double)(sampleRate * channels));
            return new DecodedAudioSource(
                Path.GetFileName(filePath),
                new TestSampleProvider(samples.ToArray(), sampleRate, channels),
                duration,
                new AudioFileFormat(
                    AudioContainerType.Wav,
                    AudioCodecType.Pcm,
                    ".wav"),
                new MemoryStream(),
                () => Open(filePath));
        }
    }

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
