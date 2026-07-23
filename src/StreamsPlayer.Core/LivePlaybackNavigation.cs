namespace StreamsPlayer.Core;

/// <summary>
/// Pure Previous/Next cursor logic for the single live audio session (SP-0021). Given the
/// captured launch order as an availability mask (an entry is <c>false</c> when its row has
/// since been hidden or deleted), it finds the nearest still-available neighbour. It never
/// wraps: reaching either end returns <c>null</c> so the caller stops cleanly.
/// </summary>
public static class LivePlaybackNavigation
{
    /// <summary>
    /// The first available index strictly after <paramref name="current"/>, or <c>null</c> at the end.
    /// A <paramref name="current"/> of -1 (the playing channel is no longer in the captured order)
    /// scans from the start.
    /// </summary>
    public static int? NextAvailable(int current, IReadOnlyList<bool> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        for (var index = current + 1; index < available.Count; index++)
        {
            if (available[index])
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>
    /// The first available index strictly before <paramref name="current"/>, or <c>null</c> at the start.
    /// A <paramref name="current"/> of -1 returns <c>null</c>: there is no defined previous when the
    /// current channel has fallen out of the captured order.
    /// </summary>
    public static int? PreviousAvailable(int current, IReadOnlyList<bool> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        var start = current < 0 ? -1 : current - 1;
        for (var index = start; index >= 0; index--)
        {
            if (available[index])
            {
                return index;
            }
        }

        return null;
    }
}
