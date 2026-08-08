namespace StreamsPlayer.Core;

/// <summary>
/// One rung of one source, and how many times it has refused to play. Keyed by <b>bandwidth</b> rather
/// than by ladder position: a source may re-encode between sessions, and a remembered index would then
/// blame whichever rendition happens to sit at that slot today.
/// </summary>
public sealed record QualityRungMemory(int BandwidthBps, int Failures);

/// <summary>
/// What one source's rungs have been observed to do, and when that was last true. The timestamp is what
/// lets the record expire: a network, a route or an origin changes, and evidence from last week must not
/// silently cap a stream that has since become deliverable.
/// </summary>
/// <param name="Ceiling">
/// SP-0076: the restriction that was in effect when this record was last written - the value a new session
/// can open with instead of paying a re-open to rediscover it. Null means the session was on the top rung
/// and there was nothing to cap to; it is also what a document written before this field existed
/// deserializes to, and both mean the same thing to a reader: no blind cap. It is a full rung rather than
/// the bandwidth <see cref="QualityRungMemory"/> keys on, because the engines take the ceiling as a
/// resolution and a bandwidth cannot be turned into one without the ladder this record does not carry.
/// </param>
public sealed record ChannelQualityMemory(
    string Url,
    DateTimeOffset UpdatedAt,
    List<QualityRungMemory> Rungs,
    StreamQualityRung? Ceiling = null);

/// <summary>Why <see cref="QualityMemory.Recall"/> is or is not handing back a ceiling.</summary>
/// <remarks>
/// SP-0076 criterion 2: an archived log has to separate "the rule did not apply" from "the rule never
/// ran", so every one of these reaches the log rather than only the one that caps something.
/// </remarks>
public enum QualityCeilingRecall
{
    /// <summary>Nothing is remembered for this source, or the whole record has expired.</summary>
    NoRecord,

    /// <summary>
    /// A record exists but names no restriction: either it was written before the ceiling was recorded, or
    /// the session it came from ended on the top rung. Neither may be applied blindly.
    /// </summary>
    NoCeiling,

    /// <summary>
    /// A ceiling is recorded, but older than one may be trusted without a single observation behind it.
    /// The failure counts in the same record are still current - see <see cref="QualityMemory.Retention"/>.
    /// </summary>
    Stale,

    /// <summary>A ceiling is recorded and current; the next open may start there.</summary>
    Applied
}

/// <summary>
/// What one source's record still says today: the probe waits to seed, the ceiling to open with, and why
/// there is no ceiling when there is none.
/// </summary>
public readonly record struct QualityRecollection(
    IReadOnlyDictionary<int, int> Failures,
    StreamQualityRung? Ceiling,
    QualityCeilingRecall CeilingRecall)
{
    /// <summary>No evidence at all - what an absent, expired or unreadable record reads as.</summary>
    public static QualityRecollection Nothing { get; } =
        new(new Dictionary<int, int>(), null, QualityCeilingRecall.NoRecord);
}

/// <summary>
/// SP-0071: the failure record <see cref="AdaptiveQualityGovernor"/> starts a session with.
///
/// <para>Measured on the owner's own 2026-08-08 session: the governor's memory died with the player
/// window, so every fresh open of the same channel paid one probe five minutes in - 4.0 s of black to go
/// up plus 17.5 s to come back down, <b>60 % of that session's entire black screen spent re-learning what
/// the previous session already knew</b>. This is the state that survives.</para>
///
/// <para>Local, private application data with no interface of its own: never uploaded, bounded, and
/// self-expiring. It records what a source did, not what the user wants - there is still no per-channel
/// quality setting.</para>
///
/// <para>SP-0076 widened what it changes. It used to seed only <em>how eagerly a rung that has already
/// refused is tried again</em>, which left every session opening on the top rung and paying one re-open -
/// 3.0 to 18.5 s of black screen - to arrive where the previous session already knew it belonged. The
/// record now also carries the restriction that was in effect, so a new session can open there. That
/// second use is a cap applied before a single observation, so it is trusted for
/// <see cref="BlindCeilingRetention"/> rather than for <see cref="Retention"/>.</para>
/// </summary>
public static class QualityMemory
{
    /// <summary>
    /// How many sources are remembered, most-recently-updated first. A pathological source is a handful
    /// of channels, not a viewing history; the cap is what keeps this a cache rather than a second one.
    /// </summary>
    public const int MaxSources = 200;

    /// <summary>
    /// How long a record stays true. A week is long enough to span the usual viewing rhythm and short
    /// enough that an origin which was fixed on Monday is not still being punished the following month.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>
    /// SP-0076: how long the recorded ceiling may be applied to an open that has observed nothing yet.
    /// <para>Deliberately shorter than <see cref="Retention"/>, because the two facts in one record are not
    /// equally safe. A failure count only changes how eagerly a rung is retried, and being wrong costs one
    /// delayed probe. A cap applied before a single observation decides what the user sees for the whole
    /// session and he has no way to know it happened. A day also gives the ticket its own criterion for
    /// free: a source that stopped throttling returns to full quality within twenty-four hours even if no
    /// probe ever succeeds.</para>
    /// </summary>
    public static readonly TimeSpan BlindCeilingRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// What this source's record still says: the failure counts to seed a governor with, by rung
    /// bandwidth, and the ceiling a new session may open at. An absent, expired or empty record reads as
    /// <see cref="QualityRecollection.Nothing"/>, which is exactly "no evidence" - every rung at the base
    /// wait and no cap.
    /// </summary>
    public static QualityRecollection Recall(
        IEnumerable<ChannelQualityMemory> entries,
        string url,
        DateTimeOffset now)
    {
        var identity = CatalogUrlIdentity.Normalize(url);
        if (identity.Length == 0)
        {
            return QualityRecollection.Nothing;
        }

        var entry = entries.FirstOrDefault(item =>
            string.Equals(CatalogUrlIdentity.Normalize(item.Url), identity, StringComparison.Ordinal));
        if (entry is null || IsExpired(entry, now))
        {
            return QualityRecollection.Nothing;
        }

        var recalled = new Dictionary<int, int>();
        foreach (var rung in entry.Rungs)
        {
            if (rung.Failures > 0)
            {
                recalled[rung.BandwidthBps] = rung.Failures;
            }
        }

        var (ceiling, outcome) = RecallCeiling(entry, now);
        return new QualityRecollection(recalled, ceiling, outcome);
    }

    /// <summary>
    /// The ceiling half of a record that is otherwise still current, under its own shorter lifetime.
    /// A rung with no usable resolution is treated as unrecorded rather than as a cap of zero: the value
    /// reaches the engines as a resolution, and a zero one would exclude every rendition there is.
    /// </summary>
    private static (StreamQualityRung? Ceiling, QualityCeilingRecall Outcome) RecallCeiling(
        ChannelQualityMemory entry,
        DateTimeOffset now)
    {
        if (entry.Ceiling is not { Width: > 0, Height: > 0 } ceiling)
        {
            return (null, QualityCeilingRecall.NoCeiling);
        }

        return now - entry.UpdatedAt >= BlindCeilingRetention
            ? (null, QualityCeilingRecall.Stale)
            : (ceiling, QualityCeilingRecall.Applied);
    }

    /// <summary>
    /// The whole file after this source's record is replaced by <paramref name="rungs"/>. Replaced rather
    /// than merged: the governor holds the complete truth for the ladder it is on, and a rung it has
    /// forgiven must actually lose its record instead of being re-seeded from the file it just wrote.
    /// <para>Expired records are dropped and the list is capped on every write, so the file is pruned by
    /// ordinary use and never needs a maintenance pass of its own.</para>
    /// </summary>
    /// <param name="ceiling">
    /// SP-0076: the restriction in effect at this moment, or null on the top rung. Required rather than
    /// optional although null is the common value: there is exactly one caller, and an argument that could
    /// be forgotten would quietly erase the source's ceiling on the next write it makes.
    /// </param>
    public static IReadOnlyList<ChannelQualityMemory> Record(
        IEnumerable<ChannelQualityMemory> entries,
        string url,
        IReadOnlyList<QualityRungMemory> rungs,
        StreamQualityRung? ceiling,
        DateTimeOffset now)
    {
        var identity = CatalogUrlIdentity.Normalize(url);
        var kept = entries
            .Where(entry => !IsExpired(entry, now))
            .Where(entry => identity.Length == 0
                || !string.Equals(CatalogUrlIdentity.Normalize(entry.Url), identity, StringComparison.Ordinal))
            .ToList();

        var recorded = rungs.Where(rung => rung.Failures > 0).ToList();
        if (identity.Length > 0 && recorded.Count > 0)
        {
            kept.Add(new ChannelQualityMemory(identity, now, recorded, ceiling));
        }

        return kept
            .OrderByDescending(entry => entry.UpdatedAt)
            .Take(MaxSources)
            .ToArray();
    }

    private static bool IsExpired(ChannelQualityMemory entry, DateTimeOffset now) =>
        now - entry.UpdatedAt >= Retention;
}
