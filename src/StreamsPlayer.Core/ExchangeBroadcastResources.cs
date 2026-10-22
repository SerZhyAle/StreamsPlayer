namespace StreamsPlayer.Core;

/// <summary>
/// The refusal of the next local listener past <see cref="ExchangeBroadcastResources.MaximumForwarders"/>. A type of
/// its own, so a caller tells the bound from any other failure of a start by what it is and never by reading its text.
/// </summary>
public sealed class ExchangeBroadcastCapacityException() : InvalidOperationException(ExchangeBroadcastResources.CapacityReason);

/// <summary>Bounds simultaneous relay/tunnel listeners across all player windows, regardless of server.</summary>
public static class ExchangeBroadcastResources
{
    public const int MaximumForwarders = 16;

    /// <summary>The reason a leg reports when this bound refused its listener; also the text of <see cref="ExchangeBroadcastCapacityException"/>.</summary>
    public const string CapacityReason = "broadcast-forwarder-capacity";

    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim Slots = new(MaximumForwarders, MaximumForwarders);

    internal static T Start<T>(Func<IDisposable, T> start)
    {
        if (!Slots.Wait(0))
            throw new ExchangeBroadcastCapacityException();
        var lease = new Lease();
        try { return start(lease); }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private sealed class Lease : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Slots.Release();
        }
    }
}
