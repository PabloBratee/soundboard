using System.IO;
using System.Text.Json;

namespace Soundboard.App.Storage;

public sealed class ApplicationSettingsStore : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

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
            return (
                Validate(settings ?? ApplicationSettings.Default),
                null);
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
                        Validate(settings),
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
        ApplicationSettings settings)
    {
        return settings with
        {
            MicrophoneVolume = Math.Clamp(
                settings.MicrophoneVolume,
                0d,
                2d),
            SoundVolume = Math.Clamp(settings.SoundVolume, 0d, 2d),
            WindowWidth = ValidateDimension(
                settings.WindowWidth,
                minimum: 760d),
            WindowHeight = ValidateDimension(
                settings.WindowHeight,
                minimum: 680d)
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
