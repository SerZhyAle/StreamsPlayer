namespace StreamsPlayer.Core;

/// <summary>
/// SP-0084: where one channel's player window was left, keyed by the channel's normalized URL.
/// </summary>
/// <remarks>
/// Keyed by URL rather than by <see cref="StreamChannel.Id"/> on purpose. A refresh that drops a row
/// carrying nothing the user made deletes it outright, and the same URL arriving in a later bank is
/// minted a fresh <see cref="Guid"/> - so an id-keyed memory would silently lose the window of a channel
/// the user would say never went anywhere. The normalized URL survives that round trip, which is what
/// acceptance criterion 5 actually asks for.
/// </remarks>
public sealed record ChannelWindowGeometry(
    string Url,
    DateTimeOffset UpdatedAt,
    double Left,
    double Top,
    double Width,
    double Height);

/// <summary>
/// SP-0084: the pure part of remembering a player window - what is worth storing, what is worth trusting
/// on the way back out, and how the list is kept from growing without bound.
/// </summary>
/// <remarks>
/// Deliberately free of any screen. Whether a remembered rectangle still fits a monitor is
/// <see cref="ScreenPlacement.Fit"/>'s question and the App's to ask; whether it is a rectangle at all is
/// this one's, and that part can be proved without a second monitor to unplug - which the ticket names as
/// its main risk.
/// </remarks>
public static class PlayerWindowGeometry
{
    /// <summary>
    /// The most channels remembered at once, oldest write evicted first.
    /// </summary>
    /// <remarks>
    /// The ticket rules out clearing a record because its channel left the catalog - a channel can come
    /// back, and absence is not authority to forget. That leaves the growth risk to be answered some
    /// other way, and the answer is this cap: a record is created by closing a window, so the list is
    /// already proportional to the channels actually opened rather than to a bank of twenty thousand.
    /// Two hundred distinct channels opened in windows is far past any real session, and evicting the
    /// least recently left one costs the user the channel they have gone longest without watching.
    /// The same number and the same reasoning as the quality memory next door.
    /// </remarks>
    public const int MaxChannels = 200;

    /// <summary>
    /// The rectangle remembered for this channel, or <c>null</c> when there is none or it is not a
    /// rectangle at all. A stored NaN, infinity or non-positive extent reads as no memory: the file is
    /// hand-editable and survives across builds, and a window opened at NaN is not recoverable by the
    /// user, so a nonsense record has to degrade to the default placement rather than be applied.
    /// </summary>
    public static ScreenRect? Recall(IReadOnlyList<ChannelWindowGeometry> entries, string url)
    {
        var key = CatalogUrlIdentity.Normalize(url);
        if (key.Length == 0)
        {
            return null;
        }

        foreach (var entry in entries)
        {
            if (!string.Equals(CatalogUrlIdentity.Normalize(entry.Url), key, StringComparison.Ordinal))
            {
                continue;
            }

            var rectangle = new ScreenRect(entry.Left, entry.Top, entry.Width, entry.Height);
            return IsUsable(rectangle) ? rectangle : null;
        }

        return null;
    }

    /// <summary>
    /// This channel's record replaced by <paramref name="rectangle"/>, the list trimmed to
    /// <see cref="MaxChannels"/>. A rectangle that is not one is refused: the caller reads a maximized or
    /// full-screen window's bounds from the frame's restore rectangle, and a window that has never had a
    /// restored size can hand back nonsense rather than a position.
    /// </summary>
    public static IReadOnlyList<ChannelWindowGeometry> Record(
        IReadOnlyList<ChannelWindowGeometry> entries,
        string url,
        ScreenRect rectangle,
        DateTimeOffset now)
    {
        var key = CatalogUrlIdentity.Normalize(url);
        if (key.Length == 0 || !IsUsable(rectangle))
        {
            return entries;
        }

        var kept = entries
            .Where(entry => !string.Equals(CatalogUrlIdentity.Normalize(entry.Url), key, StringComparison.Ordinal))
            .ToList();

        kept.Add(new ChannelWindowGeometry(
            key,
            now,
            rectangle.Left,
            rectangle.Top,
            rectangle.Width,
            rectangle.Height));

        if (kept.Count <= MaxChannels)
        {
            return kept;
        }

        // Newest write kept. Ordering here rather than trusting the file's order, because the file is the
        // one thing on disk a user can edit by hand.
        return TrimNewest(kept, now);
    }

    /// <summary>
    /// The <see cref="MaxChannels"/> newest records, newest first by <see cref="ChannelWindowGeometry.UpdatedAt"/>.
    /// SP-0184 (S15-1): a stamp later than <paramref name="now"/> - a hand-edited file, or a clock that was
    /// wrong when the record was written - cannot be believed, and trusted at face value it would outrank
    /// every real record forever and keep its channel while the ones actually being watched were evicted. Such
    /// a record ranks below every dated one, so it is the first to go when the list is over the cap. Ties go to
    /// the later entry in the list, which is the record just written (<see cref="Record"/>) or the session's
    /// (<see cref="Merge"/>).
    /// </summary>
    private static List<ChannelWindowGeometry> TrimNewest(List<ChannelWindowGeometry> entries, DateTimeOffset now) =>
        entries
            .Select((entry, index) => (Entry: entry, Index: index))
            .OrderByDescending(item => item.Entry.UpdatedAt > now ? DateTimeOffset.MinValue : item.Entry.UpdatedAt)
            .ThenByDescending(item => item.Index)
            .Take(MaxChannels)
            .Select(item => item.Entry)
            .ToList();

    /// <summary>
    /// SP-0184: the placements a file finally yielded combined with the ones this session recorded while it
    /// could not be read. A channel present in both keeps the session's record - it is the newer one - and the
    /// list is trimmed to <see cref="MaxChannels"/> newest first, as <see cref="Record"/> does.
    /// </summary>
    public static IReadOnlyList<ChannelWindowGeometry> Merge(
        IReadOnlyList<ChannelWindowGeometry> stored,
        IReadOnlyList<ChannelWindowGeometry> session,
        DateTimeOffset? now = null)
    {
        var sessionKeys = session
            .Select(entry => CatalogUrlIdentity.Normalize(entry.Url))
            .ToHashSet(StringComparer.Ordinal);
        var merged = stored
            .Where(entry => !sessionKeys.Contains(CatalogUrlIdentity.Normalize(entry.Url)))
            .Concat(session)
            .ToList();
        return merged.Count <= MaxChannels
            ? merged
            : TrimNewest(merged, now ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// True when the rectangle is one: every extent finite, and a positive width and height. Position may
    /// legitimately be negative - a monitor left of the primary one starts there.
    /// </summary>
    public static bool IsUsable(ScreenRect rectangle) =>
        double.IsFinite(rectangle.Left) &&
        double.IsFinite(rectangle.Top) &&
        double.IsFinite(rectangle.Width) &&
        double.IsFinite(rectangle.Height) &&
        rectangle.Width > 0 &&
        rectangle.Height > 0;
}
