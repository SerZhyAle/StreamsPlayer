using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0071: the one gate over <c>quality-memory.json</c> for the whole process.
///
/// <para>Static because the file is: several player windows can be open on several channels at once, and
/// each write is a read-modify-write of the whole (small) document. Without a single gate held across
/// both halves, two windows recording at the same moment would each write the list they read, and one
/// channel's record would vanish. The catalog store solves the identical problem the identical way.</para>
/// </summary>
internal static class QualityMemoryFile
{
    private static readonly QualityMemoryStore Store = new(AppPaths.DataDirectory);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// What earlier sessions recorded for this source: the probe waits to seed and the ceiling to open at.
    /// <see cref="QualityRecollection.Nothing"/> for anything unknown, expired or unreadable - all of which
    /// mean the same thing to the governor: no evidence.
    /// </summary>
    internal static async Task<QualityRecollection> RecallAsync(string url, DateTimeOffset now)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = await Store.LoadAsync().ConfigureAwait(false);
            return QualityMemory.Recall(entries, url, now);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Replaces this source's record. Returns false when the write did not land, which the caller logs
    /// rather than surfaces: a cache that cannot be written costs one relearned probe, not playback.
    /// </summary>
    internal static async Task<bool> RecordAsync(
        string url,
        IReadOnlyList<QualityRungMemory> rungs,
        StreamQualityRung? ceiling,
        DateTimeOffset now)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = await Store.LoadAsync().ConfigureAwait(false);
            return await Store.SaveAsync(QualityMemory.Record(entries, url, rungs, ceiling, now))
                .ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
