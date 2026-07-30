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
            await using var stream = new FileStream(
                SettingsFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var settings = await JsonSerializer
                .DeserializeAsync<ApplicationSettings>(
                    stream,
                    JsonOptions,
                    cancellationToken);
            var validated = Validate(
                settings ?? ApplicationSettings.Default,
                out var warning);
            return (
                validated,
                warning);
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

        var targetLufs = settings.NormalizationTargetLufs;
        if (!double.IsFinite(targetLufs)
            || targetLufs
                < LoudnessNormalizationSettings.MinimumTargetLufs
            || targetLufs
                > LoudnessNormalizationSettings.MaximumTargetLufs)
        {
            targetLufs = LoudnessNormalizationSettings.DefaultTargetLufs;
            warnings.Add(
                "The saved loudness target was invalid and was reset to "
                + "-16 LUFS.");
        }

        var limiterCeiling = settings.SafetyLimiterCeilingDbfs;
        if (!double.IsFinite(limiterCeiling)
            || limiterCeiling < SamplePeakLimiter.MinimumCeilingDbfs
            || limiterCeiling > SamplePeakLimiter.MaximumCeilingDbfs)
        {
            limiterCeiling = SamplePeakLimiter.DefaultCeilingDbfs;
            warnings.Add(
                "The saved safety-limiter ceiling was invalid and was reset "
                + "to -1.0 dBFS.");
        }

        warning = warnings.Count == 0
            ? null
            : string.Join(" ", warnings);
        return settings with
        {
            MicrophoneVolume = Math.Clamp(
                settings.MicrophoneVolume,
                0d,
                2d),
            SoundVolume = Math.Clamp(settings.SoundVolume, 0d, 2d),
            MonitorVolume = Math.Clamp(settings.MonitorVolume, 0d, 2d),
            StopSoundHotkey = stopSoundHotkey,
            NormalizationTargetLufs = targetLufs,
            SafetyLimiterCeilingDbfs = limiterCeiling,
            WindowWidth = ValidateDimension(
                settings.WindowWidth,
                minimum: MinimumWindowWidth),
            WindowHeight = ValidateDimension(
                settings.WindowHeight,
                minimum: MinimumWindowHeight)
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
