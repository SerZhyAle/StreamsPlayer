namespace StreamsPlayer.Core;

/// <summary>
/// SP-0201 requirement 4 - what a directory frame may do to the user's library. One frame may mark a channel
/// that came from the directory as ended and may refresh a stored channel by <c>sourceId</c>; it may never
/// delete a row, never add one, and never mark ended a row the user authored - a manual row, a file, a pasted
/// link. Those rules are the reason this lives in Core: they bind whatever UI is open, and a background push
/// reaches the library only through here.
/// </summary>
public static class ExchangeDirectoryChannels
{
    /// <summary>
    /// Marks as ended every directory-origin channel whose broadcast is no longer live - a removal, or
    /// absence after a <c>list</c>. The row itself is returned unchanged in place: it keeps its id, which is
    /// what makes its collections and history reattach by themselves. Rows already ended and rows that never
    /// came from the directory are never touched, and nothing is ever removed from <paramref name="channels"/>.
    /// </summary>
    public static IReadOnlyList<StreamChannel> MarkEnded(
        IEnumerable<StreamChannel> channels,
        IReadOnlyCollection<string> liveSourceIds,
        IReadOnlyCollection<string> liveBroadcastIds,
        DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(liveSourceIds);
        ArgumentNullException.ThrowIfNull(liveBroadcastIds);

        var marked = new List<StreamChannel>();
        foreach (var channel in channels)
        {
            var broadcast = channel.FastMediaSorterBroadcast;
            if (broadcast?.DirectoryBroadcastId is not { } origin || broadcast.DirectoryEndedAt is not null)
            {
                continue;
            }

            var live = (broadcast.SourceId is { } sourceId && liveSourceIds.Contains(sourceId))
                || liveBroadcastIds.Contains(origin);
            if (!live)
            {
                marked.Add(channel with
                {
                    FastMediaSorterBroadcast = broadcast with { DirectoryEndedAt = endedAt }
                });
            }
        }

        return marked;
    }

    /// <summary>
    /// The one act a directory frame may perform on the library besides ending: replace a stored channel
    /// with the same <c>sourceId</c> (a new address, a new endpoint) without adding anything the user did
    /// not keep - SP-0201 open question 1, Option A. The frame's records are applied oldest first, so of
    /// two records one <c>sourceId</c> keeps, the later <c>updatedAt</c> wins (LIVE-BROADCAST item G).
    /// A record that finds no row does nothing.
    /// </summary>
    public static IReadOnlyList<StreamChannel> RefreshStored(
        IEnumerable<StreamChannel> channels,
        IEnumerable<ExchangeBroadcastView> broadcasts,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(broadcasts);

        var current = channels.ToList();
        foreach (var view in broadcasts.OrderBy(view => view.Record.UpdatedAt))
        {
            if (view.Support != ExchangeBroadcastSupport.Supported || view.Record.Descriptor is not { } descriptor)
            {
                continue;
            }

            var applied = FastMediaSorterBroadcastImport.ApplyStored(
                current, descriptor, now, view.Record.BroadcastId);
            if (applied is not null)
            {
                current = [.. applied.Channels];
            }
        }

        return current;
    }
}
