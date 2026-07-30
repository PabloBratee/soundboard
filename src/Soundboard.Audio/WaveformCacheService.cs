using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Soundboard.Audio;

public sealed class WaveformCacheService
{
    public const int WaveformDataVersion = 1;
    public const int DefaultBinCount = 1200;
    public const int MinimumBinCount = 100;
    public const int MaximumBinCount = 4000;
    public const long MaximumCacheFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAudioFileDecoderFactory decoderFactory;
    private readonly ConcurrentDictionary<
        string,
        Lazy<Task<WaveformLoadResult>>> pending = new(
            StringComparer.OrdinalIgnoreCase);

    public WaveformCacheService(
        string? rootPath = null,
        IAudioFileDecoderFactory? decoderFactory = null)
    {
        var applicationRoot = rootPath
            ?? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Soundboard");
        WaveformsPath = Path.Combine(applicationRoot, "Waveforms");
        this.decoderFactory =
            decoderFactory ?? AudioFileDecoderFactory.Default;
    }

    public string WaveformsPath { get; }

    public string GetCacheFilePath(string contentHash, int binCount)
    {
        ValidateBinCount(binCount);
        return Path.Combine(
            WaveformsPath,
            BuildCacheFileName(
                contentHash,
                binCount,
                WaveformDataVersion));
    }

    public static string BuildCacheFileName(
        string contentHash,
        int binCount,
        int waveformVersion)
    {
        ValidateBinCount(binCount);
        if (waveformVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(waveformVersion));
        }

        var safeHash = GetSafeHash(contentHash);
        return $"{safeHash}-v{waveformVersion}-b{binCount}.json";
    }

    public async Task<WaveformLoadResult> GetOrCreateAsync(
        string filePath,
        string contentHash,
        int binCount = DefaultBinCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ValidateBinCount(binCount);
        var cachePath = GetCacheFilePath(contentHash, binCount);
        var lazy = pending.GetOrAdd(
            cachePath,
            _ => new Lazy<Task<WaveformLoadResult>>(
                () => GetOrCreateCoreAsync(
                    filePath,
                    contentHash,
                    binCount,
                    cachePath),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        _ = task.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                pending.TryRemove(
                    new KeyValuePair<
                        string,
                        Lazy<Task<WaveformLoadResult>>>(cachePath, lazy));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken);
    }

    public IReadOnlyList<string> DeleteForContentHash(string contentHash)
    {
        var warnings = new List<string>();
        if (!Directory.Exists(WaveformsPath))
        {
            return warnings;
        }

        var safeHash = GetSafeHash(contentHash);
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         WaveformsPath,
                         $"{safeHash}-v*-b*.json",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception exception)
                    when (exception is IOException
                        or UnauthorizedAccessException)
                {
                    warnings.Add(
                        $"Waveform cache file \"{Path.GetFileName(path)}\" "
                        + $"could not be deleted: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(
                "Waveform cache files could not be enumerated for removal: "
                + exception.Message);
        }

        return warnings;
    }

    public IReadOnlyList<string> CleanupOrphans(
        IEnumerable<string> activeContentHashes)
    {
        ArgumentNullException.ThrowIfNull(activeContentHashes);
        var warnings = new List<string>();
        if (!Directory.Exists(WaveformsPath))
        {
            return warnings;
        }

        var activeHashes = activeContentHashes
            .Select(GetSafeHash)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         WaveformsPath,
                         "*.json",
                         SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                var separatorIndex = fileName.IndexOf(
                    "-v",
                    StringComparison.Ordinal);
                if (separatorIndex > 0
                    && activeHashes.Contains(fileName[..separatorIndex]))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch (Exception exception)
                    when (exception is IOException
                        or UnauthorizedAccessException)
                {
                    warnings.Add(
                        $"Orphaned waveform cache file \"{fileName}\" could "
                        + $"not be deleted: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(
                "Waveform cache files could not be enumerated for cleanup: "
                + exception.Message);
        }

        return warnings;
    }

    private async Task<WaveformLoadResult> GetOrCreateCoreAsync(
        string filePath,
        string contentHash,
        int binCount,
        string cachePath)
    {
        string? warning = null;
        if (File.Exists(cachePath))
        {
            try
            {
                if (new FileInfo(cachePath).Length > MaximumCacheFileBytes)
                {
                    throw new InvalidDataException(
                        "The cached waveform exceeds the safe size limit.");
                }

                await using var stream = new FileStream(
                    cachePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var document = await JsonSerializer.DeserializeAsync<
                    WaveformCacheDocument>(
                    stream,
                    JsonOptions);
                var cached = ValidateCacheDocument(
                    document,
                    contentHash,
                    binCount);
                return new WaveformLoadResult(
                    cached,
                    LoadedFromCache: true,
                    Warning: null);
            }
            catch (Exception exception)
                when (exception is JsonException
                    or IOException
                    or UnauthorizedAccessException
                    or InvalidDataException)
            {
                warning =
                    "The waveform cache was corrupt or unreadable and was "
                    + $"regenerated: {exception.Message}";
            }
        }

        var waveform = await Task.Run(
            () => Generate(filePath, binCount));
        try
        {
            await SaveAsync(cachePath, contentHash, waveform);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            warning = string.IsNullOrEmpty(warning)
                ? "The waveform was generated but could not be cached: "
                    + exception.Message
                : warning + " The rebuilt waveform could not be cached: "
                    + exception.Message;
        }

        return new WaveformLoadResult(
            waveform,
            LoadedFromCache: false,
            warning);
    }

    private WaveformData Generate(string filePath, int binCount)
    {
        using var decoded = decoderFactory.Open(filePath);
        var sampleRate = decoded.SampleRate;
        var channelCount = decoded.ChannelCount;
        if (channelCount is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"Waveform generation does not support "
                + $"{channelCount}-channel audio.");
        }

        var expectedFrames = Math.Max(
            1,
            AudioSamplePosition.TimeToFramePosition(
                decoded.Duration,
                sampleRate));
        var peaks = new float[binCount];
        var buffer = new float[8192 - (8192 % channelCount)];
        long framePosition = 0;
        int read;
        while ((read = decoded.SampleProvider.Read(
                   buffer,
                   0,
                   buffer.Length)) > 0)
        {
            var completeSamples = read - (read % channelCount);
            for (var sampleOffset = 0;
                 sampleOffset < completeSamples;
                 sampleOffset += channelCount)
            {
                var peak = 0f;
                for (var channel = 0; channel < channelCount; channel++)
                {
                    peak = Math.Max(
                        peak,
                        Math.Min(
                            1f,
                            Math.Abs(buffer[sampleOffset + channel])));
                }

                var bin = (int)Math.Min(
                    binCount - 1L,
                    checked(framePosition * binCount) / expectedFrames);
                peaks[bin] = Math.Max(peaks[bin], peak);
                framePosition = checked(framePosition + 1);
            }
        }

        if (framePosition == 0)
        {
            throw new InvalidDataException(
                "The decoder produced no PCM samples for the waveform.");
        }

        return new WaveformData(
            WaveformDataVersion,
            binCount,
            sampleRate,
            channelCount,
            decoded.Duration,
            Array.AsReadOnly(peaks));
    }

    private static WaveformData ValidateCacheDocument(
        WaveformCacheDocument? document,
        string contentHash,
        int binCount)
    {
        if (document is null
            || document.Version != WaveformDataVersion
            || document.BinCount != binCount
            || document.SampleRate <= 0
            || document.ChannelCount is < 1 or > 2
            || document.SourceDuration <= TimeSpan.Zero
            || !string.Equals(
                document.ContentHash,
                contentHash,
                StringComparison.OrdinalIgnoreCase)
            || document.Peaks is null
            || document.Peaks.Length != binCount
            || document.Peaks.Any(
                peak => !float.IsFinite(peak) || peak is < 0f or > 1.5f))
        {
            throw new InvalidDataException(
                "The cached waveform metadata or peak data is invalid.");
        }

        return new WaveformData(
            document.Version,
            document.BinCount,
            document.SampleRate,
            document.ChannelCount,
            document.SourceDuration,
            Array.AsReadOnly(document.Peaks));
    }

    private static async Task SaveAsync(
        string cachePath,
        string contentHash,
        WaveformData waveform)
    {
        var directory = Path.GetDirectoryName(cachePath)
            ?? throw new InvalidOperationException(
                "The waveform cache path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = cachePath + $".tmp-{Guid.NewGuid():N}";
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
                    new WaveformCacheDocument(
                        waveform.Version,
                        contentHash,
                        waveform.BinCount,
                        waveform.SampleRate,
                        waveform.ChannelCount,
                        waveform.SourceDuration,
                        waveform.Peaks.ToArray()),
                    JsonOptions);
                await stream.FlushAsync();
            }

            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A stale temporary cache file is safe to ignore.
            }
        }
    }

    private static string GetSafeHash(string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        if (contentHash.Length == 64
            && contentHash.All(Uri.IsHexDigit))
        {
            return contentHash.ToUpperInvariant();
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(contentHash)));
    }

    private static void ValidateBinCount(int binCount)
    {
        if (binCount is < MinimumBinCount or > MaximumBinCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(binCount),
                $"Waveform bins must be between {MinimumBinCount} "
                + $"and {MaximumBinCount}.");
        }
    }

    private sealed record WaveformCacheDocument(
        int Version,
        string ContentHash,
        int BinCount,
        int SampleRate,
        int ChannelCount,
        TimeSpan SourceDuration,
        float[] Peaks);
}
