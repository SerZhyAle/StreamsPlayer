namespace StreamsPlayer.Core;

/// <summary>
/// SP-0171: turns an expensive comparer into a cheap integer sort key. A list of N rows usually holds only
/// a few dozen <em>distinct</em> values of the column being sorted (the bank has ~60 rubrics over tens of
/// thousands of channels), so the comparer is run over the distinct values once and every row then sorts
/// by a rank that is a dictionary read.
/// </summary>
/// <remarks>
/// Handing the expensive comparer straight to <c>ThenBy</c> runs it once per comparison - N log N calls -
/// and for a label comparer that resolves a localized string on the UI thread, that is hundreds of
/// thousands of resource lookups per rebuild. The ranks are built per rebuild and never stored, so a
/// language change (which changes what the comparer returns) needs no invalidation.
/// </remarks>
public static class DistinctKeyRanking
{
    /// <summary>
    /// Maps every distinct key (ordinal) to its rank under <paramref name="comparer"/>. Keys the comparer
    /// calls equal share a rank, so sorting by rank keeps the stable order of ties exactly as sorting with
    /// the comparer would.
    /// </summary>
    public static Dictionary<string, int> Build(IEnumerable<string> keys, IComparer<string> comparer)
    {
        var distinct = new HashSet<string>(keys, StringComparer.Ordinal);
        var ordered = distinct.OrderBy(key => key, comparer).ToList();
        var ranks = new Dictionary<string, int>(ordered.Count, StringComparer.Ordinal);
        var rank = 0;
        for (var index = 0; index < ordered.Count; index++)
        {
            if (index > 0 && comparer.Compare(ordered[index - 1], ordered[index]) != 0)
            {
                rank++;
            }

            ranks[ordered[index]] = rank;
        }

        return ranks;
    }
}
