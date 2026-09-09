using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0084: the one gate over <c>player-geometry.json</c> for the whole process.
/// </summary>
/// <remarks>
/// <para>Static because the file is: several player windows can be open on several channels at once, and
/// each write is a read-modify-write of the whole (small) document. Without a single gate held across
/// both halves, two windows recording at the same moment would each write the list they read, and one
/// channel's placement would vanish. The quality memory next door solves the identical problem the
/// identical way.</para>
///
/// <para>Recall is synchronous and the write is not, which is the one place this differs from that
/// neighbour - and the difference is the point. A window has to be placed <i>before</i> it is shown or
/// the user watches it jump from the centre to where it belongs, and there is no awaiting anything on
/// the way to <c>Show()</c> without that jump. So the file is read once at startup into a cache this
/// process is the sole writer of, reads are served from it, and the disk write happens after the window
/// has already gone. A recall before the priming read has landed returns nothing, which is the default
/// placement - the correct answer for the very first window of a session that opened before its own
/// memory did.</para>
/// </remarks>
internal static class PlayerGeometryFile
{
    private static readonly PlayerWindowGeometryStore Store = new(AppPaths.DataDirectory);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Written only under Gate or on the UI thread at Record; read on the UI thread. Replaced wholesale
    // rather than mutated, so a reader always sees one consistent list.
    private static volatile IReadOnlyList<ChannelWindowGeometry> _cache = [];

    /// <summary>Reads the file into the cache. Called once during startup; failures leave the cache empty.</summary>
    internal static async Task PrimeAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _cache = await Store.LoadAsync().ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Where this channel's window was left, or <c>null</c> for a channel never opened in one - which the
    /// caller reads as the default placement. Whether the rectangle still fits a monitor is not decided
    /// here; that needs a screen, and this needs to be free of one.
    /// </summary>
    internal static ScreenRect? Recall(string url) => PlayerWindowGeometry.Recall(_cache, url);

    /// <summary>
    /// Records where this channel's window was left. The cache is updated synchronously, so a window
    /// closed and reopened inside one session is placed correctly even if the disk write is still in
    /// flight or never lands; the returned task is the disk half.
    /// </summary>
    internal static Task RecordAsync(string url, ScreenRect rectangle, DateTimeOffset now)
    {
        var updated = PlayerWindowGeometry.Record(_cache, url, rectangle, now);
        if (ReferenceEquals(updated, _cache))
        {
            return Task.CompletedTask; // refused as not a rectangle - nothing to write
        }

        _cache = updated;
        return FlushAsync(updated);
    }

    private static async Task FlushAsync(IReadOnlyList<ChannelWindowGeometry> entries)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Deliberately writes the list handed in rather than re-reading: this process is the only
            // writer, and the cache it came from is already the merge of every window that has closed.
            await Store.SaveAsync(entries).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
