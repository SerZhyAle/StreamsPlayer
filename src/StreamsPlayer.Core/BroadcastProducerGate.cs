namespace StreamsPlayer.Core;

/// <summary>The refusal a start request can meet, each with its own localized key (the consent tests read these).</summary>
public enum BroadcastStartRefusal
{
    /// <summary>The Settings permission is off (the owner's first consent step).</summary>
    NotAllowed,

    /// <summary>The bound of simultaneous broadcasts (SP-0217 open question 1: four sources).</summary>
    TooManySources,

    /// <summary>The source is already live.</summary>
    AlreadyRunning,

    /// <summary>SP-0221: this PC is on a server only as a guest, and a guest account never produces (item AO).</summary>
    GuestAccount
}

/// <summary>
/// SP-0217 constraint 7 as code: nothing is captured until the user has allowed broadcasting and
/// started a source, every start is the user's explicit act, and with "start when the application
/// starts" on only the sources the user chose earlier start - and only once the account is online.
/// The service owns no consent decision of its own; it asks this gate, so the UI and the restart
/// path cannot disagree.
/// </summary>
public static class BroadcastProducerGate
{
    /// <summary>A source must have a reachable LAN listener or a session able to carry a sealed broadcast.</summary>
    public static bool IsReachable(string? lanAddress, bool exchangeOnline, bool mayBroadcast, bool serverSeals,
        bool ownKeyMissing, IReadOnlyCollection<string> features) =>
        lanAddress is not null || exchangeOnline && mayBroadcast && serverSeals && !ownKeyMissing
            && (features.Contains("relay") || features.Contains("tunnel"));

    public static BroadcastStartRefusal? RefusalFor(BroadcastProducerSettings settings, BroadcastSource source,
        IReadOnlyCollection<BroadcastSource> running, bool guestOnly = false)
    {
        if (!settings.Allowed)
        {
            return BroadcastStartRefusal.NotAllowed;
        }

        if (guestOnly)
        {
            // SP-0223 requirement 4: no source is offered to a guest session, so none is started behind the UI either.
            return BroadcastStartRefusal.GuestAccount;
        }

        if (running.Contains(source))
        {
            return BroadcastStartRefusal.AlreadyRunning;
        }

        if (running.Count >= BroadcastSources.MaximumSimultaneous)
        {
            return BroadcastStartRefusal.TooManySources;
        }

        return null;
    }

    /// <summary>
    /// Requirement 7: the sources a restart starts - only when the permission and the restart setting
    /// are both on, and only the sources the user left chosen. With the restart setting off this is
    /// empty, so nothing starts.
    /// </summary>
    public static IReadOnlyList<BroadcastSource> RestartSources(BroadcastProducerSettings settings)
    {
        if (!settings.Allowed || !settings.StartWithApplication)
        {
            return [];
        }

        return settings.RestartSources
            .Select(name => BroadcastSources.TryParseWireName(name, out var source) ? source : (BroadcastSource?)null)
            .Where(source => source.HasValue)
            .Select(source => source!.Value)
            .Distinct()
            .Take(BroadcastSources.MaximumSimultaneous)
            .ToArray();
    }

    /// <summary>
    /// What a start and a stop leave behind in `RestartSources`: a start chooses its source for the
    /// next restart, a stop unchooses it - "the sources the user chose earlier", no more.
    /// </summary>
    public static IReadOnlyList<string> ChosenAfter(BroadcastProducerSettings settings, BroadcastSource source, bool started)
    {
        var wireName = BroadcastSources.WireName(source);
        var names = settings.RestartSources.Where(name => name != wireName).ToList();
        if (started)
        {
            names.Add(wireName);
        }

        return names.Take(BroadcastSources.MaximumSimultaneous).ToArray();
    }
}
