namespace StreamsPlayer.Core;

/// <summary>What a failed FastMediaSorter broadcast leg means for the listener.</summary>
public enum FastMediaSorterPlaybackFailureKind
{
    /// <summary>A stall, clean end, reset or refused connection: spend the bounded reconnect budget.</summary>
    Recoverable,

    /// <summary>The device refused this listener because its listener slots are taken. Never retried.</summary>
    ListenerLimit
}

/// <summary>
/// SP-0099: the FastMediaSorter-specific reading of a playback failure. The generic policy treats every 5xx
/// as transient; on a watch a <c>503</c> is the fifth listener, and retrying it only knocks on a full door.
/// Holds no I/O - the status comes from the single request the playback leg already made.
/// </summary>
public static class FastMediaSorterPlaybackFailure
{
    public const int ListenerLimitStatusCode = 503;

    public static FastMediaSorterPlaybackFailureKind Classify(int? firstResponseStatusCode) =>
        firstResponseStatusCode == ListenerLimitStatusCode
            ? FastMediaSorterPlaybackFailureKind.ListenerLimit
            : FastMediaSorterPlaybackFailureKind.Recoverable;
}
