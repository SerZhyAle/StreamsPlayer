namespace StreamsPlayer.Core;

/// <summary>
/// Coordinates incoming cast offers to ensure at most one confirmation is on screen at a time,
/// folding repeated offers for the same broadcast and queueing distinct ones (SP-0205 requirement 2).
/// </summary>
public sealed class ExchangeCastCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private PendingPrompt? _activePrompt;
    private readonly Queue<PendingPrompt> _queue = new();

    private sealed class PendingPrompt(ExchangeCastOffer offer, TaskCompletionSource<bool> tcs)
    {
        public string BroadcastId => offer.BroadcastId;
        public ExchangeCastOffer InitialOffer => offer;
        public List<ExchangeCastOffer> FoldedOffers { get; } = [offer];
        public TaskCompletionSource<bool> Tcs => tcs;
    }

    public async Task<bool> RequestDecisionAsync(
        ExchangeCastOffer offer,
        Func<ExchangeCastOffer, CancellationToken, Task<bool>> promptUser,
        CancellationToken cancellationToken)
    {
        Task<bool>? foldedTask = null;
        PendingPrompt pending;
        lock (_sync)
        {
            if (_activePrompt is not null && !string.IsNullOrEmpty(offer.BroadcastId) && _activePrompt.BroadcastId == offer.BroadcastId)
            {
                _activePrompt.FoldedOffers.Add(offer);
                foldedTask = _activePrompt.Tcs.Task;
            }
            else
            {
                foreach (var item in _queue)
                {
                    if (!string.IsNullOrEmpty(offer.BroadcastId) && item.BroadcastId == offer.BroadcastId)
                    {
                        item.FoldedOffers.Add(offer);
                        foldedTask = item.Tcs.Task;
                        break;
                    }
                }
            }

            if (foldedTask is null)
            {
                pending = new PendingPrompt(offer, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                _queue.Enqueue(pending);
            }
        }

        if (foldedTask is not null)
        {
            return await foldedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                _queue.TryDequeue(out _activePrompt);
            }

            if (_activePrompt is null)
            {
                return false;
            }

            var decision = false;
            try
            {
                decision = await promptUser(_activePrompt.InitialOffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                decision = false;
            }
            finally
            {
                _activePrompt.Tcs.TrySetResult(decision);
            }

            return decision;
        }
        finally
        {
            lock (_sync)
            {
                _activePrompt = null;
            }
            _gate.Release();
        }
    }

    public void CancelAll()
    {
        lock (_sync)
        {
            _activePrompt?.Tcs.TrySetResult(false);
            _activePrompt = null;
            while (_queue.TryDequeue(out var pending))
            {
                pending.Tcs.TrySetResult(false);
            }
        }
    }
}
