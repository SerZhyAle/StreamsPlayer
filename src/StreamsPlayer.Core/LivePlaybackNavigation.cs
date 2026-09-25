namespace StreamsPlayer.Core;

/// <summary>
/// Pure Previous/Next cursor logic for the single live audio session (SP-0021). Given the
/// captured launch order as an availability mask (an entry is <c>false</c> when its row has
/// since been hidden or deleted), it finds the nearest still-available neighbour. It never
/// wraps: reaching either end returns <c>null</c> so the caller stops cleanly.
/// </summary>
/// <remarks>
/// SP-0132: the predicate overloads are the ones the application uses. A materialized mask costs one
/// availability check per captured entry, and the compact panel asks on every status, now-playing and
/// volume change - with the full bank that was a whole-catalog scan per entry. The predicate is consulted
/// only for the indices the scan actually visits, which is normally one.
/// </remarks>
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
        return NextAvailable(current, available.Count, index => available[index]);
    }

    /// <inheritdoc cref="NextAvailable(int, IReadOnlyList{bool})"/>
    /// <param name="current">The current index, or -1.</param>
    /// <param name="count">The length of the captured order.</param>
    /// <param name="isAvailable">Asked once per visited index, never for an index the scan does not reach.</param>
    public static int? NextAvailable(int current, int count, Func<int, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);
        for (var index = current + 1; index < count; index++)
        {
            if (isAvailable(index))
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
        return PreviousAvailable(current, index => available[index]);
    }

    /// <inheritdoc cref="PreviousAvailable(int, IReadOnlyList{bool})"/>
    /// <param name="current">The current index, or -1.</param>
    /// <param name="isAvailable">Asked once per visited index, never for an index the scan does not reach.</param>
    public static int? PreviousAvailable(int current, Func<int, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);
        var start = current < 0 ? -1 : current - 1;
        for (var index = start; index >= 0; index--)
        {
            if (isAvailable(index))
            {
                return index;
            }
        }

        return null;
    }
}
