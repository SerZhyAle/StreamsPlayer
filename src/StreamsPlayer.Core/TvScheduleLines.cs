namespace StreamsPlayer.Core;

/// <summary>
/// SP-0171: the rows that currently show a "now on air" line, and the schedule channel each one is bound
/// to. The 30-second tick walks this set and nothing else, so its cost follows the number of bound
/// channels (tens) and not the catalog (tens of thousands), and it parses no address: the schedule channel
/// was resolved when the row was bound, against the index's own normalised keys.
/// </summary>
/// <remarks>
/// A row enters the set in <see cref="Attach"/> (created or re-addressed) or <see cref="Reconcile"/> (the
/// index was rebuilt), and leaves it in <see cref="Forget"/>. Only the last two resolve addresses, and
/// both run on an event - a catalog or binding change - never on the clock. Single-threaded by design,
/// like the list it serves.
/// </remarks>
public sealed class TvScheduleLines<TRow> where TRow : notnull
{
    private Dictionary<TRow, TvScheduleChannel> _bound = [];

    /// <summary>Rows that currently have a schedule channel; what a tick touches.</summary>
    public int BoundCount => _bound.Count;

    /// <summary>
    /// Binds one row against <paramref name="index"/> and shows its title (or clears a stale one).
    /// </summary>
    public void Attach(TRow row, string url, TvScheduleIndex index, DateTimeOffset now, Action<TRow, string?> show)
    {
        if (ResolveChannel(index, url) is { } channel)
        {
            _bound[row] = channel;
            show(row, TitleOn(channel, now));
        }
        else if (_bound.Remove(row))
        {
            show(row, null);
        }
    }

    /// <summary>Drops a row that has left the list for good.</summary>
    public void Forget(TRow row) => _bound.Remove(row);

    /// <summary>
    /// Re-binds every row after the index was rebuilt (a new schedule, a binding edit, a catalog change).
    /// The one pass that resolves addresses in bulk - and it is skipped altogether while the index matches
    /// nothing, which is every catalog without a downloaded guide.
    /// </summary>
    public void Reconcile(
        IEnumerable<TRow> rows, Func<TRow, string> urlOf, TvScheduleIndex index, DateTimeOffset now, Action<TRow, string?> show)
    {
        var previous = _bound;
        _bound = [];
        if (index.MatchedCount == 0)
        {
            foreach (var row in previous.Keys)
            {
                show(row, null);
            }

            return;
        }

        foreach (var row in rows)
        {
            if (ResolveChannel(index, urlOf(row)) is { } channel)
            {
                _bound[row] = channel;
                show(row, TitleOn(channel, now));
            }
            else if (previous.ContainsKey(row))
            {
                show(row, null);
            }
        }
    }

    /// <summary>Refreshes the title of every bound row for <paramref name="now"/>. Parses no address.</summary>
    public void Tick(DateTimeOffset now, Action<TRow, string?> show)
    {
        foreach (var (row, channel) in _bound)
        {
            show(row, TitleOn(channel, now));
        }
    }

    private static TvScheduleChannel? ResolveChannel(TvScheduleIndex index, string url) =>
        index.HasSchedule && index.MatchedCount > 0 ? index.ChannelFor(url) : null;

    private static string? TitleOn(TvScheduleChannel channel, DateTimeOffset now) =>
        TvScheduleQueries.NowAndNext(channel.Programmes, now).Now?.Title;
}
