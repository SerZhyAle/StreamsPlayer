using System.Threading;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0118: the session-local named lock the first copy holds for its lifetime (<c>APP-ACTIVATION</c>
/// rule 2). Its name comes from <see cref="StreamsPlayer.Core.SingleInstanceIdentity"/> and is frozen.
/// </summary>
/// <remarks>
/// A mutex is owned by a thread, so it is taken and released on the UI thread - in
/// <c>App.OnStartup</c> and <c>App.OnExit</c>. A lock left behind by a copy that crashed is abandoned,
/// not held: taking it over is correct, since no process is using the state any more.
/// </remarks>
internal sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstanceLock(Mutex mutex) => _mutex = mutex;

    /// <summary>The lock when this is the first copy; <see langword="null"/> when another copy holds it.</summary>
    internal static SingleInstanceLock? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero))
            {
                return new SingleInstanceLock(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstanceLock(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread any more; disposing the handle releases it all the same.
        }

        _mutex.Dispose();
    }
}
