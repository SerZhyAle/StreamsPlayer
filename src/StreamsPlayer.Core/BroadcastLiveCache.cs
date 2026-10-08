namespace StreamsPlayer.Core;

/// <summary>
/// The live buffer a FastMediaSorter video or RTSP broadcast is opened with (SP-0208).
/// <c>LIVE-BROADCAST</c> consumer rule 1 (as amended by item I) asks for the first picture within the producer's
/// keyframe interval plus 1 s, and rule 2 for no more than 2 s behind the source on a LAN. A live engine does not
/// start before its network buffer is full and a real-time source fills it in real time, so the buffer is also the
/// wait before the first picture: the fixed 15 s that suits a flaky catalog stream cannot meet either rule. The
/// producer states the latency it can sustain in <c>targetLatencyMs</c>; this takes it, with a floor against Wi-Fi
/// jitter and a ceiling that keeps the buffer inside rule 2. A catalog stream, an audio broadcast (its own
/// transport) and a channel with no descriptor are not touched.
/// <para>SP-0203: the buffer follows the endpoint the leg actually opens (rule 4 - raise it only to that
/// endpoint's own <c>targetLatencyMs</c>), and an exchange endpoint (<c>RELAY</c>/<c>TUNNEL</c>) may go to
/// 2000 ms, the value the contract names for the relay.</para>
/// </summary>
public static class BroadcastLiveCache
{
    /// <summary>Below this a Wi-Fi hop's jitter shows as stutter, whatever the producer asks for.</summary>
    public const uint FloorMilliseconds = 500;

    /// <summary>Above this the steady state would leave rule 2's 2 s on a LAN, with no room for decode and render.</summary>
    public const uint CeilingMilliseconds = 1_500;

    /// <summary>The exchange transports' ceiling: rule 4 names 2000 for <c>RELAY</c> (item D) and <c>TUNNEL</c>.</summary>
    public const uint ExchangeCeilingMilliseconds = 2_000;

    /// <summary>A descriptor that states no <c>targetLatencyMs</c>: the HTTP MPEG-TS figure of item H.</summary>
    public const uint UnstatedMilliseconds = 1_000;

    /// <summary>The buffer for this channel, or <c>null</c> when the product's own setting applies.</summary>
    public static uint? For(StreamChannel channel) => For(channel, endpoint: null);

    /// <summary>
    /// The buffer for the endpoint <paramref name="endpoint"/> of this channel - the one the leg is
    /// about to open - or <c>null</c> when the product's own setting applies. A null endpoint keeps
    /// the channel-level reading.
    /// </summary>
    public static uint? For(StreamChannel channel, FastMediaSorterBroadcastEndpoint? endpoint)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var broadcast = channel.FastMediaSorterBroadcast;
        if (broadcast is null || channel.MediaKind == MediaKind.Audio)
        {
            return null;
        }

        var ceiling = endpoint is not null && IsExchangeTransport(endpoint.Transport)
            ? ExchangeCeilingMilliseconds
            : CeilingMilliseconds;
        var stated = endpoint?.TargetLatencyMs ?? broadcast.TargetLatencyMs;
        if (stated is not { } latency || latency <= 0)
        {
            return UnstatedMilliseconds;
        }

        return (uint)Math.Clamp(latency, FloorMilliseconds, ceiling);
    }

    private static bool IsExchangeTransport(string? transport) =>
        transport is not null &&
        (transport.Equals("RELAY", StringComparison.OrdinalIgnoreCase) ||
         transport.Equals("TUNNEL", StringComparison.OrdinalIgnoreCase));
}
