namespace StreamsPlayer.Core;

/// <summary>
/// Pure recency/retention rules for the local listening history (SP-0019). Every method returns a new
/// list and never mutates its input, so the caller folds the result into <see cref="CatalogState"/>
/// and persists it. No platform or UI dependency — history is captured only where playback already
/// reaches its successful-play sink; this type does not decide when a play succeeds.
/// </summary>
public static class ListeningHistory
{
    /// <summary>Maximum number of channel entries retained; older entries are evicted on overflow.</summary>
    public const int MaxEntries = 100;

    /// <summary>
    /// Record a successful play of <paramref name="channelId"/>. Moves an existing entry for the same
    /// channel to the front (carrying its previous <see cref="ListeningHistoryEntry.LastTrackText"/> so
    /// a fresh session does not blank the last-known song line before ICY re-populates it), or inserts a
    /// new one, then truncates to <see cref="MaxEntries"/>. Title and media kind come from the current play.
    /// </summary>
    public static List<ListeningHistoryEntry> RecordPlay(
        IReadOnlyList<ListeningHistoryEntry> history,
        Guid channelId,
        string title,
        MediaKind mediaKind,
        DateTimeOffset playedAt)
    {
        var previous = history.FirstOrDefault(entry => entry.ChannelId == channelId);
        var promoted = new ListeningHistoryEntry
        {
            ChannelId = channelId,
            Title = title,
            MediaKind = mediaKind,
            LastPlayedAt = playedAt,
            LastTrackText = previous?.LastTrackText
        };

        var result = new List<ListeningHistoryEntry>(Math.Min(history.Count + 1, MaxEntries)) { promoted };
        foreach (var entry in history)
        {
            if (entry.ChannelId == channelId)
            {
                continue;
            }

            if (result.Count == MaxEntries)
            {
                break;
            }

            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Update the last observed now-playing text for <paramref name="channelId"/> without reordering or
    /// adding a row. Returns <see langword="null"/> when the channel is absent or the text is unchanged,
    /// so the caller can skip a redundant save; otherwise a new list with only that entry replaced.
    /// </summary>
    public static List<ListeningHistoryEntry>? UpdateTrackText(
        IReadOnlyList<ListeningHistoryEntry> history,
        Guid channelId,
        string? trackText)
    {
        var index = -1;
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].ChannelId == channelId)
            {
                index = i;
                break;
            }
        }

        if (index < 0 || history[index].LastTrackText == trackText)
        {
            return null;
        }

        var result = new List<ListeningHistoryEntry>(history);
        result[index] = result[index] with { LastTrackText = trackText };
        return result;
    }
}
