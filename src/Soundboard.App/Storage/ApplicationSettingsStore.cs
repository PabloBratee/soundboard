using System.IO;
using System.Text.Json;
using Soundboard.App.Hotkeys;
using Soundboard.Audio;

namespace Soundboard.App.Storage;

public sealed class ApplicationSettingsStore : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Smallest restorable window size. Must stay in step with the
    /// MinWidth and MinHeight declared by MainWindow.
    /// </summary>
    public const double MinimumWindowWidth = 880d;

    /// <inheritdoc cref="MinimumWindowWidth" />
    public const double MinimumWindowHeight = 620d;

    private readonly SemaphoreSlim saveGate = new(1, 1);
    private bool disposed;

    public ApplicationSettingsStore(string? rootPath = null)
    {
        RootPath = rootPath
            ?? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Soundboard");
        SettingsFilePath = Path.Combine(RootPath, "settings.json");
    }

    public string RootPath { get; }

    public string SettingsFilePath { get; }

    public async Task<(ApplicationSettings Settings, string? Warning)>
        LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!File.Exists(SettingsFilePath))
        {
            return (ApplicationSettings.Default, null);
        }

        try
        {
            ApplicationSettings validated;
            string? migrationWarning;
            string? warning;
            bool needsSave;
            await using (var stream = new FileStream(
                SettingsFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken))
            {
                var settings = document.RootElement
                    .Deserialize<ApplicationSettings>(JsonOptions);
                var migrated = Migrate(
                    settings ?? ApplicationSettings.Default,
                    document.RootElement,
                    out migrationWarning,
                    out needsSave);
                validated = Validate(migrated, out warning);
            }

            var combinedWarnings = new[] { migrationWarning, warning }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (needsSave)
            {
                try
                {
                    await SaveAsync(validated, cancellationToken);
                }
                catch (Exception exception)
                    when (exception is IOException
                        or UnauthorizedAccessException)
                {
                    combinedWarnings.Add(
                        "Migrated settings are active in memory but could not "
                        + $"be saved: {exception.Message}");
                }
            }

            return (
                validated,
                combinedWarnings.Count == 0
                    ? null
                    : string.Join(" ", combinedWarnings));
        }
        catch (Exception exception)
            when (exception is JsonException
                or IOException
                or UnauthorizedAccessException)
        {
            return (
                ApplicationSettings.Default,
                "Settings could not be loaded; safe defaults are in use: "
                + exception.Message);
        }
    }

    public async Task SaveAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await saveGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            Directory.CreateDirectory(RootPath);
            var temporaryPath = SettingsFilePath
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
                        Validate(settings, out _),
                        JsonOptions,
                        cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                if (File.Exists(SettingsFilePath))
                {
                    File.Replace(
                        temporaryPath,
                        SettingsFilePath,
                        destinationBackupFileName: null,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, SettingsFilePath);
                }
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }
        finally
        {
            saveGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            saveGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static ApplicationSettings Validate(
        ApplicationSettings settings,
        out string? warning)
    {
        var warnings = new List<string>();
        HotkeyGesture? stopSoundHotkey = null;
        if (!HotkeyGesture.TryNormalize(
                settings.StopSoundHotkey,
                out stopSoundHotkey,
                out var hotkeyError))
        {
            warnings.Add(
                "The saved Stop Sound hotkey is invalid and was ignored: "
                + hotkeyError);
        }

        if (!HotkeyGesture.TryNormalize(
                settings.PauseResumeHotkey,
                out var pauseResumeHotkey,
                out var pauseHotkeyError))
        {
            warnings.Add(
                "The saved Pause/Resume playback hotkey is invalid and was "
                + "ignored: " + pauseHotkeyError);
        }

        warning = warnings.Count == 0
            ? null
            : string.Join(" ", warnings);
        return settings with
        {
            SchemaVersion = ApplicationSettings.CurrentSchemaVersion,
            SoundVolume = ValidateVolume(settings.SoundVolume),
            MonitorVolume = ValidateVolume(settings.MonitorVolume),
            VoicePrioritySensitivity = Enum.IsDefined(
                settings.VoicePrioritySensitivity)
                    ? settings.VoicePrioritySensitivity
                    : VoiceSensitivity.Normal,
            VoicePriorityStrength = Enum.IsDefined(
                settings.VoicePriorityStrength)
                    ? settings.VoicePriorityStrength
                    : VoiceDuckingStrength.Balanced,
            StopSoundHotkey = stopSoundHotkey,
            PauseResumeHotkey = pauseResumeHotkey,
            WindowWidth = ValidateDimension(
                settings.WindowWidth,
                minimum: MinimumWindowWidth),
            WindowHeight = ValidateDimension(
                settings.WindowHeight,
                minimum: MinimumWindowHeight)
        };
    }

    private static ApplicationSettings Migrate(
        ApplicationSettings settings,
        JsonElement root,
        out string? warning,
        out bool needsSave)
    {
        var version = root.TryGetProperty("schemaVersion", out var versionElement)
            && versionElement.TryGetInt32(out var parsedVersion)
                ? parsedVersion
                : 1;
        if (version >= ApplicationSettings.CurrentSchemaVersion)
        {
            warning = null;
            needsSave = false;
            return settings;
        }

        var hadSavedMicrophone = !string.IsNullOrWhiteSpace(
            settings.MicrophoneEndpointId);
        var hadSavedVirtualOutput = !string.IsNullOrWhiteSpace(
            settings.VirtualOutputEndpointId);
        warning = "Migrated settings to automatic audio startup. Obsolete "
            + "microphone gain, normalization, and limiter settings were removed.";
        needsSave = true;
        return settings with
        {
            SchemaVersion = ApplicationSettings.CurrentSchemaVersion,
            UseDefaultMicrophone = !hadSavedMicrophone,
            SetupCompleted = hadSavedMicrophone && hadSavedVirtualOutput
        };
    }

    private static double? ValidateDimension(
        double? value,
        double minimum)
    {
        return value is { } dimension
            && double.IsFinite(dimension)
            && dimension >= minimum
                ? dimension
                : null;
    }

    private static double ValidateVolume(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, 0d, 1d)
            : 1d;
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
            // A later startup can safely ignore a temporary settings file.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
