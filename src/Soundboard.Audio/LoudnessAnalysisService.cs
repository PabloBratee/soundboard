using System.Collections.Concurrent;
using System.Text.Json;

namespace Soundboard.Audio;

public sealed class LoudnessAnalysisService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly LoudnessAnalyzer analyzer;
    private readonly SemaphoreSlim analysisGate;
    private readonly ConcurrentDictionary<
        string,
        Lazy<Task<LoudnessAnalysisOutcome>>> requests = new();
    private readonly CancellationTokenSource shutdownCancellation = new();
    private bool disposed;
    private long analysisExecutionCount;

    public LoudnessAnalysisService(
        string? rootPath = null,
        IAudioFileDecoderFactory? decoderFactory = null,
        int maximumConcurrency = 1)
    {
        if (maximumConcurrency is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        }

        RootPath = rootPath
            ?? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Soundboard");
        AnalysisPath = Path.Combine(RootPath, "Analysis");
        analyzer = new LoudnessAnalyzer(decoderFactory);
        analysisGate = new SemaphoreSlim(
            maximumConcurrency,
            maximumConcurrency);
    }

    public string RootPath { get; }

    public string AnalysisPath { get; }

    public long AnalysisExecutionCount =>
        Interlocked.Read(ref analysisExecutionCount);

    public string GetCacheFilePath(LoudnessAnalysisKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Path.Combine(AnalysisPath, $"{key.GetStableId()}.json");
    }

    public async Task<LoudnessAnalysisOutcome?> TryLoadCachedAsync(
        LoudnessAnalysisKey key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var lookup = await ReadCacheAsync(key, cancellationToken);
        return lookup.Result is null
            ? null
            : new LoudnessAnalysisOutcome(
                key,
                lookup.Result,
                LoadedFromCache: true,
                lookup.Warning);
    }

    public async Task<LoudnessAnalysisOutcome> GetOrAnalyzeAsync(
        LoudnessAnalysisKey key,
        string filePath,
        AudioClipSettings clipSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(clipSettings);
        ThrowIfDisposed();

        var stableId = key.GetStableId();
        var request = requests.GetOrAdd(
            stableId,
            _ => new Lazy<Task<LoudnessAnalysisOutcome>>(
                () => AnalyzeAndCacheAsync(
                    key,
                    filePath,
                    clipSettings,
                    shutdownCancellation.Token),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await request.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            requests.TryRemove(
                new KeyValuePair<
                    string,
                    Lazy<Task<LoudnessAnalysisOutcome>>>(stableId, request));
        }
    }

    public IReadOnlyList<string> CleanupOrphans(
        IEnumerable<string> activeContentHashes)
    {
        ThrowIfDisposed();
        var warnings = new List<string>();
        if (!Directory.Exists(AnalysisPath))
        {
            return warnings;
        }

        var active = activeContentHashes.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(
                     AnalysisPath,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(path);
                var document = JsonSerializer.Deserialize<CacheDocument>(
                    json,
                    JsonOptions);
                if (document?.Key is null
                    || !IsCacheDocumentValid(document)
                    || !active.Contains(document.Key.ContentHash))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception)
                when (exception is JsonException
                    or IOException
                    or UnauthorizedAccessException)
            {
                TryDeleteFile(path);
                warnings.Add(
                    $"Ignored a corrupt loudness-analysis cache entry "
                    + $"{Path.GetFileName(path)}; it will be regenerated "
                    + $"when needed: {exception.Message}");
            }
        }

        return warnings;
    }

    public IReadOnlyList<string> DeleteForContentHash(string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ThrowIfDisposed();
        var warnings = new List<string>();
        if (!Directory.Exists(AnalysisPath))
        {
            return warnings;
        }

        foreach (var path in Directory.EnumerateFiles(
                     AnalysisPath,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                var document = JsonSerializer.Deserialize<CacheDocument>(
                    File.ReadAllText(path),
                    JsonOptions);
                if (document?.Key is not null
                    && string.Equals(
                        document.Key.ContentHash,
                        contentHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception)
                when (exception is JsonException
                    or IOException
                    or UnauthorizedAccessException)
            {
                warnings.Add(
                    $"Could not inspect or remove loudness-analysis cache "
                    + $"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        return warnings;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        shutdownCancellation.Cancel();
        var activeTasks = requests.Values
            .Where(value => value.IsValueCreated)
            .Select(value => value.Value)
            .ToArray();
        try
        {
            await Task.WhenAll(activeTasks);
        }
        catch (OperationCanceledException)
        {
            // Shutdown cancellation is expected.
        }
        finally
        {
            shutdownCancellation.Dispose();
            analysisGate.Dispose();
        }
    }

    private async Task<LoudnessAnalysisOutcome> AnalyzeAndCacheAsync(
        LoudnessAnalysisKey key,
        string filePath,
        AudioClipSettings clipSettings,
        CancellationToken cancellationToken)
    {
        var cached = await ReadCacheAsync(key, cancellationToken);
        if (cached.Result is not null)
        {
            return new LoudnessAnalysisOutcome(
                key,
                cached.Result,
                LoadedFromCache: true,
                cached.Warning);
        }

        await analysisGate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref analysisExecutionCount);
            var result = await Task.Run(
                () => analyzer.AnalyzeFile(
                    filePath,
                    clipSettings,
                    cancellationToken),
                cancellationToken);
            var warning = cached.Warning;
            try
            {
                await WriteCacheAsync(key, result, cancellationToken);
            }
            catch (Exception exception)
                when (exception is IOException
                    or UnauthorizedAccessException)
            {
                warning = string.Join(
                    " ",
                    new[]
                    {
                        warning,
                        "Loudness analysis completed, but its derived cache "
                        + $"could not be saved: {exception.Message}"
                    }.Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            return new LoudnessAnalysisOutcome(
                key,
                result,
                LoadedFromCache: false,
                warning);
        }
        finally
        {
            analysisGate.Release();
        }
    }

    private async Task<CacheLookup> ReadCacheAsync(
        LoudnessAnalysisKey key,
        CancellationToken cancellationToken)
    {
        var path = GetCacheFilePath(key);
        if (!File.Exists(path))
        {
            return new CacheLookup(null, null);
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<CacheDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            if (document is null
                || document.Key != key
                || !IsCacheDocumentValid(document))
            {
                throw new JsonException(
                    "The cached analysis document is invalid or stale.");
            }

            return new CacheLookup(document.Result, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException
                or IOException
                or UnauthorizedAccessException)
        {
            TryDeleteFile(path);
            return new CacheLookup(
                null,
                "A corrupt loudness-analysis cache entry was ignored and "
                + $"will be regenerated: {exception.Message}");
        }
    }

    private async Task WriteCacheAsync(
        LoudnessAnalysisKey key,
        LoudnessAnalysisResult result,
        CancellationToken cancellationToken)
    {
        if (!result.HasFiniteValues)
        {
            return;
        }

        Directory.CreateDirectory(AnalysisPath);
        var path = GetCacheFilePath(key);
        var temporaryPath = path + $".tmp-{Guid.NewGuid():N}";
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
                    new CacheDocument(key, result),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(path))
            {
                File.Replace(
                    temporaryPath,
                    path,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static bool IsCacheDocumentValid(CacheDocument document)
    {
        return document.Key.AlgorithmVersion
                == LoudnessAnalyzer.AlgorithmVersion
            && document.Result.AlgorithmVersion
                == LoudnessAnalyzer.AlgorithmVersion
            && document.Result.HasFiniteValues
            && document.Result.EffectiveDurationSeconds >= 0d
            && (document.Result.IsValid
                ? string.IsNullOrWhiteSpace(document.Result.InvalidReason)
                : !string.IsNullOrWhiteSpace(
                    document.Result.InvalidReason));
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
            // Derived cache cleanup is best effort.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed record CacheDocument(
        LoudnessAnalysisKey Key,
        LoudnessAnalysisResult Result);

    private sealed record CacheLookup(
        LoudnessAnalysisResult? Result,
        string? Warning);
}
