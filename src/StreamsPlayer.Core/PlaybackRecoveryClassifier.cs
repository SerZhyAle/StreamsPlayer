namespace StreamsPlayer.Core;

/// <summary>
/// Maps a <see cref="PlaybackFailureSignal"/> to the <see cref="RecoveryTrigger"/> that selects its
/// retry budget and backoff (<c>docs/specifications/streams.txt</c>, Part D). Deterministic and total.
/// </summary>
public static class PlaybackRecoveryClassifier
{
    public static RecoveryTrigger Classify(PlaybackFailureSignal signal)
    {
        if (signal.Stall)
        {
            return RecoveryTrigger.Stall;
        }

        var reason = signal.Reason?.Trim() ?? string.Empty;

        if (signal.BehindLiveWindow || ContainsAny(reason, "behind live window", "live window", "live edge"))
        {
            return RecoveryTrigger.BehindLiveWindow;
        }

        if (signal.EndReached)
        {
            return RecoveryTrigger.StreamEnded;
        }

        // An HTTP status is the authoritative retryable/hard-fail split (Part D): 429 and 5xx are
        // transient; any other 4xx is a permanent client error that must not spend the retry budget.
        if (signal.HttpStatusCode is { } status)
        {
            if (status == 429 || status is >= 500 and <= 599)
            {
                return RecoveryTrigger.Transient;
            }

            if (status is >= 400 and <= 499)
            {
                return RecoveryTrigger.HardFail;
            }
        }

        // Malformed manifest / unsupported container -> non-retryable, unless the text is clearly a
        // network fault (a transport error naming a codec should still be treated as transient).
        if (ContainsAny(reason, "unsupported", "not supported", "notsupported", "malformed", "codec", "container")
            && !ContainsAny(reason, "timeout", "connection", "network", "socket", "dns", "refused", "reset"))
        {
            return RecoveryTrigger.HardFail;
        }

        if (ContainsAny(reason, "timeout", "timed out", "connection", "network", "socket", "dns",
                "refused", "reset", "unreachable", "429", "5xx", "webexception", "httprequest"))
        {
            return RecoveryTrigger.Transient;
        }

        // Availability failures on this bank are predominantly transient; the budget still bounds them.
        return RecoveryTrigger.Transient;
    }

    private static bool ContainsAny(string value, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
