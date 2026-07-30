namespace Soundboard.App.Lifetime;

internal sealed class SingleInstanceGuard : IDisposable
{
    public const string ApplicationMutexName =
        @"Local\Pablo.Soundboard.Application.5E44D118-81F6-4BC8-B960-4FD04D09883A";

    internal const string AllowMultipleInstancesEnvironmentVariable =
        "SOUNDBOARD_ALLOW_MULTIPLE_INSTANCES";

    internal const string MutexNameEnvironmentVariable =
        "SOUNDBOARD_SINGLE_INSTANCE_MUTEX";

    private Mutex? mutex;
    private bool ownsMutex;

    private SingleInstanceGuard(Mutex? mutex, bool ownsMutex)
    {
        this.mutex = mutex;
        this.ownsMutex = ownsMutex;
    }

    public static bool TryAcquireForApplication(
        out SingleInstanceGuard? guard)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(
                    AllowMultipleInstancesEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            guard = new SingleInstanceGuard(null, ownsMutex: false);
            return true;
        }

        var mutexName = Environment.GetEnvironmentVariable(
            MutexNameEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(mutexName))
        {
            mutexName = ApplicationMutexName;
        }

        return TryAcquire(mutexName, out guard);
    }

    internal static bool TryAcquire(
        string mutexName,
        out SingleInstanceGuard? guard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);

        var candidate = new Mutex(
            initiallyOwned: true,
            mutexName,
            out var createdNew);
        if (!createdNew)
        {
            candidate.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(candidate, ownsMutex: true);
        return true;
    }

    public void Dispose()
    {
        var currentMutex = Interlocked.Exchange(ref mutex, null);
        if (currentMutex is null)
        {
            return;
        }

        if (ownsMutex)
        {
            currentMutex.ReleaseMutex();
            ownsMutex = false;
        }

        currentMutex.Dispose();
    }
}
