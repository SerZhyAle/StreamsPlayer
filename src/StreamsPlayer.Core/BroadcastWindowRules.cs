namespace StreamsPlayer.Core;

/// <summary>What one source of the broadcast window is doing right now (SP-0252 requirement 2).</summary>
public enum BroadcastSourcePhase
{
    Off,
    Starting,
    Live,
    Failed,
    Stopping
}

/// <summary>
/// SP-0252: the decisions of the broadcast window and its header button that do not depend on WPF, so the window only
/// draws them. Sources start and stop independently (owner decision 4), so every rule here is per source.
/// </summary>
public static class BroadcastWindowRules
{
    /// <summary>The order the window lists its sources in: picture first, then sound.</summary>
    public static IReadOnlyList<BroadcastSource> SourceOrder { get; } =
        [BroadcastSource.Screen, BroadcastSource.Camera, BroadcastSource.Microphone, BroadcastSource.SystemSound];

    /// <summary>
    /// The phase of one source. A stop in progress wins over everything (the source may still be running while it is
    /// torn down); a start in progress wins over a failure left by an earlier try; a running source is live; a failure
    /// that is not followed by a start is shown until the person starts again.
    /// </summary>
    public static BroadcastSourcePhase PhaseOf(bool running, bool starting, bool stopping, bool failed)
    {
        if (stopping)
        {
            return BroadcastSourcePhase.Stopping;
        }

        if (starting)
        {
            return BroadcastSourcePhase.Starting;
        }

        if (running)
        {
            return BroadcastSourcePhase.Live;
        }

        return failed ? BroadcastSourcePhase.Failed : BroadcastSourcePhase.Off;
    }

    /// <summary>
    /// Owner decision 1: the header button carries a warning mark only while a broadcast is on the air, the account is
    /// one that announces on the exchange server, and that server cannot be reached. A broadcast on the local network
    /// alone, or an account that is a guest's or switched off, never earns the mark.
    /// </summary>
    public static bool ShowsServerWarning(int liveSources, bool accountActive, bool mayBroadcast, bool sessionOnline) =>
        liveSources > 0 && accountActive && mayBroadcast && !sessionOnline;

    /// <summary>A source can be started from the window when it is off or failed and a slot is free.</summary>
    public static bool CanStart(BroadcastSourcePhase phase, int liveOrStartingSources) =>
        phase is BroadcastSourcePhase.Off or BroadcastSourcePhase.Failed
        && liveOrStartingSources < BroadcastSources.MaximumSimultaneous;
}
