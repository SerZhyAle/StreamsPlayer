namespace StreamsPlayer.Core;

/// <summary>
/// Serializes mutations of one catalog state document.
/// </summary>
public sealed class CatalogStateCommitter
{
    private readonly Func<CatalogState, CancellationToken, Task<CatalogState>> _save;
    private readonly Action<Delegate>? _mutationGuard;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CatalogState _current;
    private CatalogState _requested;

    public CatalogStateCommitter(
        CatalogState initialState,
        Func<CatalogState, CancellationToken, Task<CatalogState>> save,
        Action<Delegate>? mutationGuard = null)
    {
        _current = initialState ?? throw new ArgumentNullException(nameof(initialState));
        _requested = initialState;
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _mutationGuard = mutationGuard;
    }

    /// <summary>The last state successfully saved.</summary>
    public CatalogState Current => Volatile.Read(ref _current);

    /// <summary>The latest requested state, including a write in progress.</summary>
    public CatalogState Requested => Volatile.Read(ref _requested);

    /// <summary>
    /// Applies <paramref name="mutation"/> to the latest state and saves that result before another
    /// mutation can begin. A failed write leaves Current at the last saved state.
    /// </summary>
    public async Task<CatalogStateCommitResult> CommitAsync(
        Func<CatalogState, CatalogState> mutation,
        CancellationToken cancellationToken = default)
        => await CommitAsync(mutation, _save, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Applies a mutation using a one-off writer, for example when the state change installs an atlas in
    /// the same atomic store operation. The writer still runs under this instance's one gate.
    /// </summary>
    public async Task<CatalogStateCommitResult> CommitAsync(
        Func<CatalogState, CatalogState> mutation,
        Func<CatalogState, CancellationToken, Task<CatalogState>> save,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return await CommitAsync((state, _) => Task.FromResult(mutation(state)), save, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Applies a bounded asynchronous mutation while holding the same state gate.</summary>
    public async Task<CatalogStateCommitResult> CommitAsync(
        Func<CatalogState, CancellationToken, Task<CatalogState>> mutation,
        Func<CatalogState, CancellationToken, Task<CatalogState>> save,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(save);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
#if DEBUG
            _mutationGuard?.Invoke(mutation);
#endif
            var previous = _requested;
            var attempted = await mutation(previous, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A catalog-state mutation returned null.");
            if (ReferenceEquals(attempted, previous) && ReferenceEquals(previous, _current))
            {
                return new CatalogStateCommitResult(_current, Saved: true, Failure: null, AttemptedState: attempted);
            }

            Volatile.Write(ref _requested, attempted);
            var committed = false;
            try
            {
                var saved = await save(attempted, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A catalog-state writer returned null.");
                Volatile.Write(ref _current, saved);
                Volatile.Write(ref _requested, saved);
                committed = true;
                return new CatalogStateCommitResult(saved, Saved: true, Failure: null, AttemptedState: attempted);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new CatalogStateCommitResult(_current, Saved: false, Failure: exception, AttemptedState: attempted);
            }
            finally
            {
                if (!committed)
                {
                    Volatile.Write(ref _requested, _current);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed record CatalogStateCommitResult(
    CatalogState State, bool Saved, Exception? Failure, CatalogState AttemptedState);
