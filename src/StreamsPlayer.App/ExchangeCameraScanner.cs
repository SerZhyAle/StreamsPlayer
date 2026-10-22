using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal enum ExchangeCameraStepKind
{
    /// <summary>The frame was not looked at: a decode was already running, or the scan had ended.</summary>
    Dropped,

    /// <summary>The frame holds no code the scan can use; the next frame is the next try.</summary>
    Continue,

    /// <summary>A code was seen that is not the one wanted; <see cref="ExchangeCameraStep.HintKey"/> says why, the scan goes on.</summary>
    Hint,

    /// <summary>A connection string was seen: the scan has ended and <see cref="ExchangeCameraStep.Result"/> holds what it came to.</summary>
    Done,

    /// <summary>The time limit passed without a connection string; the scan has ended.</summary>
    TimedOut
}

internal sealed record ExchangeCameraStep(ExchangeCameraStepKind Kind, string? HintKey = null, ExchangeImportResult? Result = null)
{
    internal static readonly ExchangeCameraStep Dropped = new(ExchangeCameraStepKind.Dropped);
    internal static readonly ExchangeCameraStep Continue = new(ExchangeCameraStepKind.Continue);
    internal static readonly ExchangeCameraStep TimedOut = new(ExchangeCameraStepKind.TimedOut);
}

/// <summary>
/// SP-0229: what a live camera's pictures come to. Each frame goes through the reader and the codec of the
/// picture doors (SP-0221), with the one difference a camera forces: a person chooses a picture file, but a camera
/// sees whatever is in front of it, so a QR that is not a connection string is a hint and the scan goes on, and the
/// first connection string ends it. Reading is not connecting - the step only carries what the codec made of it.
/// No WinRT type is used here so the decisions can be proved without a camera.
/// </summary>
internal sealed class ExchangeCameraScanner
{
    /// <summary>A forgotten window must not keep a camera on: the scan ends by itself.</summary>
    internal static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(120);

    internal const string OtherCodeHint = "ExchangeCameraOtherCode";
    internal const string SeveralHint = "ExchangeCameraSeveral";

    private readonly TimeSpan _limit;
    private readonly TimeProvider _clock;
    private readonly Func<ExchangePixels, ExchangeQrOutcome> _decode;
    private readonly long _startedAt;
    private int _busy;
    private int _finished;

    internal ExchangeCameraScanner(TimeSpan? limit = null, TimeProvider? clock = null,
        Func<ExchangePixels, ExchangeQrOutcome>? decode = null)
    {
        _limit = limit ?? DefaultLimit;
        _clock = clock ?? TimeProvider.System;
        _decode = decode ?? ExchangeQrReader.Decode;
        _startedAt = _clock.GetTimestamp();
    }

    internal bool IsFinished => Volatile.Read(ref _finished) == 1;

    /// <summary>A decode is running, so a frame offered now would be dropped: the caller need not copy it.</summary>
    internal bool IsBusy => Volatile.Read(ref _busy) == 1;

    /// <summary>
    /// Looks at one frame off the calling thread. One decode runs at a time and a frame offered meanwhile is
    /// dropped, so a slow decode costs pictures and never queues them. Never throws on a hostile frame.
    /// </summary>
    internal async Task<ExchangeCameraStep> OfferAsync(ExchangePixels frame, CancellationToken cancellationToken = default)
    {
        if (IsFinished)
        {
            return ExchangeCameraStep.Dropped;
        }

        if (_clock.GetElapsedTime(_startedAt) >= _limit)
        {
            return TryFinish() ? ExchangeCameraStep.TimedOut : ExchangeCameraStep.Dropped;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return ExchangeCameraStep.Dropped;
        }

        try
        {
            ExchangeQrOutcome outcome;
            try
            {
                outcome = await Task.Run(() => _decode(frame), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ExchangeCameraStep.Dropped;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The decoder's failure on a damaged frame; its message is private input and the next frame is a new try.
                return ExchangeCameraStep.Continue;
            }

            switch (outcome.Kind)
            {
                case ExchangeQrOutcomeKind.Found when ExchangeConnectionImport.Recognizes(outcome.Text ?? ""):
                    // A string that begins like ours is final even when the codec refuses it: the person is told the
                    // codec's one message, not left scanning a code that will never be accepted.
                    return TryFinish()
                        ? new ExchangeCameraStep(ExchangeCameraStepKind.Done, Result: ExchangeConnectionImport.ReadQr(outcome))
                        : ExchangeCameraStep.Dropped;
                case ExchangeQrOutcomeKind.Found:
                    return Hint(OtherCodeHint);
                case ExchangeQrOutcomeKind.Several:
                    return Hint(SeveralHint);
                default:
                    return ExchangeCameraStep.Continue;
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Ends the scan from outside (the window closed): later frames are dropped.</summary>
    internal void Stop() => TryFinish();

    private ExchangeCameraStep Hint(string key) => IsFinished
        ? ExchangeCameraStep.Dropped
        : new ExchangeCameraStep(ExchangeCameraStepKind.Hint, HintKey: key);

    private bool TryFinish() => Interlocked.CompareExchange(ref _finished, 1, 0) == 0;
}
