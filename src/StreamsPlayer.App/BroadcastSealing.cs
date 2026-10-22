using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0226: what the application adds to <see cref="ExchangeBroadcastSealer"/> for one running broadcast - the directory
/// lease that keeps the list of the account's devices current while it runs, and the gate that serializes the sealing
/// work (a directory change, a session coming up). Every decision about keys, boxes and epochs is the sealer's, in Core.
/// Disposing it zeroes the keys and gives the lease back.
/// </summary>
internal sealed class BroadcastSealing : IDisposable
{
    internal BroadcastSealing(string broadcastId, string mediaType) =>
        Sealer = new ExchangeBroadcastSealer(broadcastId, mediaType, ExchangeTunnelCertificate.Create());

    internal ExchangeBroadcastSealer Sealer { get; }

    /// <summary>Held for as long as the broadcast runs, so the list of the account's devices is current (item DG).</summary>
    internal IDisposable? DirectoryLease { get; set; }

    /// <summary>Serializes the producer's sealing work for this broadcast.</summary>
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    public void Dispose()
    {
        DirectoryLease?.Dispose();
        DirectoryLease = null;
        Sealer.Dispose();
    }
}
