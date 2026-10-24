namespace PcDiag.Agent;

/// <summary>Ensures only one Agent runs per Windows session, using a named mutex.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Acquires the named mutex, or returns null when another instance already holds it.</summary>
    public static SingleInstance? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (createdNew)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    // No ReleaseMutex: it must run on the acquiring thread, which async continuations do not guarantee.
    // Closing the only handle destroys the named mutex, so the next Agent can start.
    public void Dispose() => _mutex.Dispose();
}
