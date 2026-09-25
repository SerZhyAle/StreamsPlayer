namespace StreamsPlayer.Core;

/// <summary>
/// Serializes mutations of one catalog state document.
/// </summary>
/// <remarks>
/// A state change becomes the current in-memory truth before its write starts. If that write fails, the
/// change is deliberately retained: the next commit retries the complete latest state instead of making a
/// successful-looking user action disappear. Callers must surface <see cref="CatalogStateCommitResult.Failure"/>
/// when that matters to the user.
/// </remarks>
public sealed class CatalogStateCommitter
{
    private readonly Func<CatalogState, CancellationToken, Task<CatalogState>> _save;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CatalogState _current;

    public CatalogStateCommitter(
        CatalogState initialState,
        Func<CatalogState, CancellationToken, Task<CatalogState>> save)
    {
        _current = initialState ?? throw new ArgumentNullException(nameof(initialState));
        _save = save ?? throw new ArgumentNullException(nameof(save));
    }

    /// <summary>The latest accepted state, including a change awaiting retry after a failed save.</summary>
    public CatalogState Current => _current;

    /// <summary>
    /// Applies <paramref name="mutation"/> to the latest state and saves that result before another
    /// mutation can begin. The caller always receives the accepted in-memory state.
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
        ArgumentNullException.ThrowIfNull(save);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _current = mutation(_current) ?? throw new InvalidOperationException("A catalog-state mutation returned null.");
            try
            {
                _current = await save(_current, cancellationToken).ConfigureAwait(false);
                return new CatalogStateCommitResult(_current, Saved: true, Failure: null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new CatalogStateCommitResult(_current, Saved: false, Failure: exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Retries the latest state without inventing a new mutation.</summary>
    public Task<CatalogStateCommitResult> RetryAsync(CancellationToken cancellationToken = default) =>
        CommitAsync(state => state, cancellationToken);
}

public sealed record CatalogStateCommitResult(CatalogState State, bool Saved, Exception? Failure);
