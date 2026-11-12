using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

// SP-0262: the answers to cast offers, run off the control stream's read loop so a question to the user never stops it.
internal sealed partial class ExchangeSourceService
{
    // Bounds on what one control stream can make this device hold open. Distinct broadcasts waiting on the
    // user are few by nature (one prompt is on screen at a time); repeats of one broadcast and the answers
    // that need no question (unsupported, auto-accept) are counted apart, so neither can crowd out the other.
    private const int MaximumPendingCastBroadcasts = 8;
    private const int MaximumAnswersPerBroadcast = 4;
    private const int MaximumLiveCastAnswers = 64;

    private readonly record struct OpenCastAnswer(string BroadcastId, bool AwaitsUser, Task Task);

    /// <summary>
    /// SP-0205 / DEVICE-EXCHANGE 7.8, run off the control stream's read loop: the question to the user can take
    /// as long as the offer window, and a loop that waits for it stops answering keepalives and reading
    /// frames. A repeated offer for the same broadcast folds into the open question in the coordinator.
    /// </summary>
    private async Task AnswerCastOfferAsync(ExchangeCastOffer offer, Func<object, CancellationToken, Task> send,
        TaskCompletionSource castFailed, CancellationToken cancellationToken, bool refuseBusy = false)
    {
        var answered = false;
        try
        {
            if (refuseBusy)
            {
                // DEVICE-EXCHANGE item R's vocabulary has no "busy"; the receiver declining is the honest one.
                await send(ExchangeCastAnswer.Decline(offer.BroadcastId, castId: offer.CastId), cancellationToken).ConfigureAwait(false);
                answered = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(offer.DeviceName) && Directory is { } dir)
            {
                var matchedGroup = dir.Groups.FirstOrDefault(g => g.DeviceId == offer.DeviceId);
                if (matchedGroup is not null && !string.IsNullOrWhiteSpace(matchedGroup.DeviceName))
                {
                    offer = offer with { DeviceName = matchedGroup.DeviceName };
                }
            }

            // SP-0226 / item DK: a cast hands the record with its box; it is opened now, before the question or the
            // auto-accept, because the answer starts the playback and the playback asks for the keys.
            await OpenOfferBoxAsync(offer, cancellationToken).ConfigureAwait(false);
            if (offer.Support != ExchangeBroadcastSupport.Supported || offer.Descriptor is null)
            {
                await send(ExchangeCastAnswer.Unsupported(offer.BroadcastId, offer.CastId), cancellationToken).ConfigureAwait(false);
                answered = true;
                return;
            }

            if (Account.AutoAcceptCasts)
            {
                await send(ExchangeCastAnswer.Accept(offer.BroadcastId, offer.CastId), cancellationToken).ConfigureAwait(false);
                answered = true;
                CastAccepted?.Invoke(offer);
                return;
            }

            var decision = CastPromptRequested is { } promptHandler
                ? await _castCoordinator.RequestDecisionAsync(offer, promptHandler, cancellationToken).ConfigureAwait(false)
                : ExchangeCastDecision.Declined;
            if (cancellationToken.IsCancellationRequested)
            {
                // The session ended while the question was open; its stream is gone and no answer is owed.
                return;
            }

            await send(ExchangeCastAnswer.For(offer, decision), cancellationToken).ConfigureAwait(false);
            answered = true;
            if (decision == ExchangeCastDecision.Accepted)
            {
                CastAccepted?.Invoke(offer);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The session ended; nothing to answer.
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            // A cast answer that cannot be written means the stream is broken: hand that to the read loop,
            // which owns reconnecting, instead of letting an answer task fail where nobody looks.
            castFailed.TrySetResult();
        }
        catch (Exception) when (!answered)
        {
            // Whatever else went wrong - the prompt handler raising from the dialog or the dispatcher is the
            // known case - the caster is owed an answer, and an unanswered offer only ends at its 60 s window.
            // The exception is not logged here: this service has no log of its own, and the App's prompt
            // reports the faults it can attribute. After the answer went out there is nothing left to owe.
            await DeclineAfterFaultAsync(offer, send, castFailed, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeclineAfterFaultAsync(ExchangeCastOffer offer, Func<object, CancellationToken, Task> send,
        TaskCompletionSource castFailed, CancellationToken cancellationToken)
    {
        try
        {
            await send(ExchangeCastAnswer.Decline(offer.BroadcastId, castId: offer.CastId), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            castFailed.TrySetResult();
        }
    }

    /// <summary>
    /// Starts the answer task for one parsed offer, within what the stream may make this device hold open. A
    /// new broadcast past the pending cap is answered declined at once; repeats of an open broadcast past
    /// theirs are dropped (that broadcast's answer is already on its way), as is anything past the ceiling.
    /// None of it is logged: a flooding server is not worth a line per frame, and the payload stays out.
    /// </summary>
    private void StartCastAnswer(List<OpenCastAnswer> open, ExchangeCastOffer offer,
        Func<object, CancellationToken, Task> send, TaskCompletionSource castFailed, CancellationToken cancellationToken)
    {
        open.RemoveAll(item => item.Task.IsCompleted);
        if (open.Count >= MaximumLiveCastAnswers
            || open.Count(item => item.BroadcastId == offer.BroadcastId) >= MaximumAnswersPerBroadcast)
        {
            return;
        }

        var awaitsUser = offer.Support == ExchangeBroadcastSupport.Supported && offer.Descriptor is not null
            && !Account.AutoAcceptCasts;
        var refuseBusy = awaitsUser
            && !open.Any(item => item.AwaitsUser && item.BroadcastId == offer.BroadcastId)
            && open.Where(item => item.AwaitsUser).Select(item => item.BroadcastId).Distinct().Count()
                >= MaximumPendingCastBroadcasts;
        open.Add(new OpenCastAnswer(offer.BroadcastId, awaitsUser && !refuseBusy,
            AnswerCastOfferAsync(offer, send, castFailed, cancellationToken, refuseBusy)));
    }

    private void HandleCastStop(JsonElement root)
    {
        if (ExchangeCastStop.TryParse(root, out var stop) && stop is not null)
        {
            CastStopped?.Invoke(stop);
        }
    }
}
