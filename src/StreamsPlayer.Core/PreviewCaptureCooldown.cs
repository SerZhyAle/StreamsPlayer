namespace StreamsPlayer.Core;

/// <summary>
/// Remembers which stream URLs just failed to yield a preview frame, so an automatic capture is not
/// attempted against them again on every scroll.
/// </summary>
/// <remarks>
/// <para>
/// Evidence, from the owner's log archive of 2026-09-06: one dead source produced eight
/// <c>PREVIEW FAIL</c> records inside a single session, two of them one second apart, because a failed
/// capture leaves no trace anywhere - the URL simply drops out of the pending set and is re-queued the
/// next time its tile is visible. Each attempt holds one of the four capture slots for up to the
/// capture service's first-frame timeout, so a handful of dead tiles can starve the live ones the user
/// is actually looking at.
/// </para>
/// <para>
/// Only the automatic path is suppressed. An explicit request - a hover dwell, an explicit refresh -
/// is the user saying "try it now", and must always reach the engine; callers express that by not
/// consulting this type, or by calling <see cref="Forget"/> first.
/// </para>
/// <para>
/// Not thread-safe by design, matching the other policy types in this library: the coordinator that
/// owns it already serializes its own state behind locks.
/// </para>
/// </remarks>
public sealed class PreviewCaptureCooldown
{
    /// <summary>
    /// How long a failed URL is left alone. Long enough that a scroll back and forth over the same dead
    /// tile costs one attempt rather than one per pass, short enough that a source which was down while
    /// the user was browsing is retried inside the same sitting.
    /// </summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTimeOffset> _failures = new(StringComparer.Ordinal);

    /// <summary>Number of URLs currently held back. Diagnostic only.</summary>
    public int Count => _failures.Count;

    /// <summary>True when an automatic capture of <paramref name="url"/> should be skipped for now.</summary>
    public bool IsSuppressed(string url, DateTimeOffset now) =>
        _failures.TryGetValue(url, out var failedAt) && now - failedAt < Cooldown;

    /// <summary>Records a failed capture and sweeps entries whose cooldown has expired.</summary>
    /// <remarks>
    /// Swept on insert for the same reason the hover throttle is (SP-0069): an entry older than the
    /// window can never suppress anything again, and a map emptied only at shutdown is bounded by the
    /// catalog rather than by the failures that filled it.
    /// </remarks>
    public void RecordFailure(string url, DateTimeOffset now)
    {
        if (_failures.Count > 0)
        {
            foreach (var expired in _failures
                .Where(entry => now - entry.Value >= Cooldown)
                .Select(entry => entry.Key)
                .ToList())
            {
                _failures.Remove(expired);
            }
        }

        _failures[url] = now;
    }

    /// <summary>Drops the record for one URL - a capture succeeded, or the user asked for it explicitly.</summary>
    public void Forget(string url) => _failures.Remove(url);

    /// <summary>Drops every record.</summary>
    public void Clear() => _failures.Clear();
}
