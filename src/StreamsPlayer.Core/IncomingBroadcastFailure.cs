namespace StreamsPlayer.Core;

/// <summary>
/// SP-0222: which failures of an incoming broadcast's leg end it at once with a message, and which spend the
/// bounded recovery budget. The windows of incoming broadcasts share these verdicts so a refusal that no retry
/// can mend (a refused password, a certificate that does not match, the bound, a full listener list) is said
/// once, in words, and never retried in the background.
/// </summary>
public static class IncomingBroadcastFailure
{
    /// <summary>"This broadcast is no longer available" - the verdict of a recovery budget that is spent.</summary>
    public const string NoLongerAvailableKey = "BroadcastNoLongerAvailable";

    /// <summary>"N broadcasts are open, the most this PC holds at once" - formatted with <see cref="IncomingBroadcastWindows.Maximum"/>.</summary>
    public const string BoundReachedKey = "BroadcastsBoundReached";

    /// <summary>The device refused this listener because its listener slots are taken.</summary>
    public const string ListenerLimitKey = "FmsBroadcastListenerLimit";

    /// <summary>
    /// The localization key of the message that ends a leg that failed for <paramref name="reason"/> with
    /// <paramref name="statusCode"/> as the first answer, or <c>null</c> when the failure is one a reconnect may mend.
    /// </summary>
    public static string? TerminalMessageKey(string? reason, int? statusCode)
    {
        if (ExchangeBroadcastAuth.MessageKey(reason) is { } password)
        {
            return password;
        }

        if (ExchangeBroadcastCertificateException.MessageKey(reason) is { } certificate)
        {
            return certificate;
        }

        if (reason == ExchangeBroadcastResources.CapacityReason)
        {
            return BoundReachedKey;
        }

        // Terminal on the first answer: every retry would itself be the fifth listener.
        return FastMediaSorterPlaybackFailure.Classify(statusCode) == FastMediaSorterPlaybackFailureKind.ListenerLimit
            ? ListenerLimitKey
            : null;
    }

    /// <summary>Whether the failure is a refused password, whose stored value is the broadcaster's old one and must be forgotten.</summary>
    public static bool IsPasswordRefusal(string? reason) => ExchangeBroadcastAuth.MessageKey(reason) is not null;
}
