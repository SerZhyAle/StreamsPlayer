namespace StreamsPlayer.Core;

/// <summary>What the user (or the clock) decided about one cast offer.</summary>
public enum ExchangeCastDecision
{
    /// <summary>The user said no, or the session ended before an answer: the answer is <c>declined</c>.</summary>
    Declined,

    /// <summary>The user accepted.</summary>
    Accepted,

    /// <summary>No answer inside the offer window: the answer is <c>timeout</c> (DEVICE-EXCHANGE item R).</summary>
    TimedOut
}

/// <summary>
/// Coordinates incoming cast offers to ensure at most one confirmation is on screen at a time,
/// folding repeated offers for the same broadcast and queueing distinct ones (SP-0205 requirement 2).
/// </summary>
/// <remarks>
/// Every offer owns one <see cref="OfferWindow"/> that starts when it arrives, so an offer that waits behind
/// another prompt spends its window waiting - the server has already answered the caster <c>timeout</c> by
/// the time it would be shown, and showing it then would be a question nobody is waiting for. The prompt is
/// handed the window's token and is expected to dismiss itself when it is cancelled; the coordinator does not
/// depend on it, because a prompt that ignores the token is abandoned at the deadline all the same.
/// </remarks>
public sealed class ExchangeCastCoordinator
{
    /// <summary>DEVICE-EXCHANGE item R: the server's working default for "no answer within 60 s".</summary>
    public static readonly TimeSpan DefaultOfferWindow = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly TimeSpan _offerWindow;
    private PendingPrompt? _activePrompt;
    private readonly List<PendingPrompt> _waiting = [];

    public ExchangeCastCoordinator() : this(DefaultOfferWindow)
    {
    }

    public ExchangeCastCoordinator(TimeSpan offerWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(offerWindow, TimeSpan.Zero);
        _offerWindow = offerWindow;
    }

    /// <summary>How long one offer may stay unanswered before it is answered <see cref="ExchangeCastDecision.TimedOut"/>.</summary>
    public TimeSpan OfferWindow => _offerWindow;

    /// <summary>
    /// How one prompt ended. <see cref="Abandoned"/> means its own window or caller gave up before anyone
    /// answered: the followers folded into it were never asked either, so they must not inherit the ending.
    /// </summary>
    private readonly record struct PromptResult(ExchangeCastDecision Decision, bool Abandoned);

    private sealed class PendingPrompt(ExchangeCastOffer offer)
    {
        public string BroadcastId => offer.BroadcastId;
        public ExchangeCastOffer InitialOffer => offer;
        public List<ExchangeCastOffer> FoldedOffers { get; } = [offer];
        public TaskCompletionSource<PromptResult> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Resolves one offer. It never throws for a cancellation: the caller's own cancellation (the session
    /// ended) reads as <see cref="ExchangeCastDecision.Declined"/>, the window running out as
    /// <see cref="ExchangeCastDecision.TimedOut"/>. A repeat of an open broadcast waits for that question's
    /// answer; if the question was abandoned before anyone answered, the repeat asks for itself, with what is
    /// left of its own window.
    /// </summary>
    public async Task<ExchangeCastDecision> RequestDecisionAsync(
        ExchangeCastOffer offer,
        Func<ExchangeCastOffer, CancellationToken, Task<bool>> promptUser,
        CancellationToken cancellationToken)
    {
        using var window = new CancellationTokenSource(_offerWindow);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, window.Token);
        var token = session.Token;

        while (true)
        {
            PendingPrompt? shared = null;
            var mine = new PendingPrompt(offer);
            lock (_sync)
            {
                if (!string.IsNullOrEmpty(offer.BroadcastId))
                {
                    shared = _activePrompt is { } active && active.BroadcastId == offer.BroadcastId
                        ? active
                        : _waiting.FirstOrDefault(item => item.BroadcastId == offer.BroadcastId);
                }

                if (shared is not null)
                {
                    shared.FoldedOffers.Add(offer);
                }
                else
                {
                    _waiting.Add(mine);
                }
            }

            if (shared is not null)
            {
                try
                {
                    var result = await shared.Tcs.Task.WaitAsync(token).ConfigureAwait(false);
                    if (!result.Abandoned)
                    {
                        return result.Decision;
                    }
                }
                catch (OperationCanceledException)
                {
                    return Outcome(cancellationToken, window);
                }

                // The leader was removed from the coordinator before it reported, so the next pass finds
                // either a newer prompt of this broadcast to fold into or nothing, and leads.
                continue;
            }

            return await LeadAsync(mine, promptUser, cancellationToken, window, token).ConfigureAwait(false);
        }
    }

    private async Task<ExchangeCastDecision> LeadAsync(
        PendingPrompt mine,
        Func<ExchangeCastOffer, CancellationToken, Task<bool>> promptUser,
        CancellationToken cancellationToken,
        CancellationTokenSource window,
        CancellationToken token)
    {
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var abandoned = Outcome(cancellationToken, window);
            lock (_sync)
            {
                _waiting.Remove(mine);
            }

            mine.Tcs.TrySetResult(new PromptResult(abandoned, Abandoned: true));
            return abandoned;
        }

        var decision = ExchangeCastDecision.Declined;
        var abandonedWhileOpen = false;
        try
        {
            lock (_sync)
            {
                _waiting.Remove(mine);
                _activePrompt = mine;
            }

            // CancelAll answered this offer while it queued; the session it belonged to is gone.
            if (mine.Tcs.Task.IsCompleted)
            {
                return (await mine.Tcs.Task.ConfigureAwait(false)).Decision;
            }

            // The window may have run out between the queue and the gate: no prompt for a dead offer.
            token.ThrowIfCancellationRequested();
            var prompt = promptUser(mine.InitialOffer, token);
            try
            {
                decision = await prompt.WaitAsync(token).ConfigureAwait(false)
                    ? ExchangeCastDecision.Accepted
                    : ExchangeCastDecision.Declined;
            }
            catch (OperationCanceledException)
            {
                // An abandoned prompt can still fault later; nobody is left to observe it.
                _ = prompt.ContinueWith(static task => task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            decision = Outcome(cancellationToken, window);
            abandonedWhileOpen = true;
        }
        finally
        {
            // Out of the coordinator first, then reported: a follower that wakes to an abandoned prompt looks
            // the broadcast up again and must not find this one.
            lock (_sync)
            {
                if (ReferenceEquals(_activePrompt, mine))
                {
                    _activePrompt = null;
                }
            }

            mine.Tcs.TrySetResult(new PromptResult(decision, abandonedWhileOpen));
            _gate.Release();
        }

        return decision;
    }

    /// <summary>The session ended: every open or queued question is answered declined and forgotten.</summary>
    public void CancelAll()
    {
        var ended = new PromptResult(ExchangeCastDecision.Declined, Abandoned: false);
        lock (_sync)
        {
            _activePrompt?.Tcs.TrySetResult(ended);
            _activePrompt = null;
            foreach (var pending in _waiting)
            {
                pending.Tcs.TrySetResult(ended);
            }

            _waiting.Clear();
        }
    }

    private static ExchangeCastDecision Outcome(CancellationToken caller, CancellationTokenSource window) =>
        window.IsCancellationRequested && !caller.IsCancellationRequested
            ? ExchangeCastDecision.TimedOut
            : ExchangeCastDecision.Declined;
}
