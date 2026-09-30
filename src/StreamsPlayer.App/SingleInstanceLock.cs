using System.IO;
using System.Threading;

namespace StreamsPlayer.App;

/// <summary>SP-0170: how an attempt on the session lock ended.</summary>
internal enum SingleInstanceLockResult
{
    /// <summary>This copy holds the lock and is the first.</summary>
    Acquired,

    /// <summary>Another copy holds it.</summary>
    HeldByAnotherCopy,

    /// <summary>
    /// The lock exists but this copy may not open it - it was created by a copy running with other rights,
    /// typically elevated. Somebody holds it; it cannot be waited for or taken over.
    /// </summary>
    Inaccessible
}

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

    /// <summary>
    /// Takes the lock, waiting up to <paramref name="wait"/> for the copy that holds it to let go (SP-0170: a
    /// copy that is closing releases it when its close work is done). Never throws for an unopenable lock.
    /// </summary>
    internal static SingleInstanceLockResult TryAcquire(string name, TimeSpan wait, out SingleInstanceLock? held)
    {
        held = null;
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or WaitHandleCannotBeOpenedException
            or IOException)
        {
            return SingleInstanceLockResult.Inaccessible;
        }

        try
        {
            if (mutex.WaitOne(wait))
            {
                held = new SingleInstanceLock(mutex);
                return SingleInstanceLockResult.Acquired;
            }
        }
        catch (AbandonedMutexException)
        {
            held = new SingleInstanceLock(mutex);
            return SingleInstanceLockResult.Acquired;
        }

        mutex.Dispose();
        return SingleInstanceLockResult.HeldByAnotherCopy;
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
