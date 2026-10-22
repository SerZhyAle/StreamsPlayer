using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0217: the answer to a cast this device sent, as `DEVICE-EXCHANGE` 7.8 writes it - `cast-result`
/// with `broadcastId`, `receiverDeviceId`, `accepted`, `reason`. The reason vocabulary is item R:
/// `unavailable`, `declined`, `timeout`, `unsupported`; an unknown value reads as the generic one.
/// </summary>
public sealed record ExchangeCastResult(
    string BroadcastId,
    string ReceiverDeviceId,
    bool Accepted,
    string Reason)
{
    private const int MaximumIdLength = 128;

    /// <summary>Each of the four refusal reasons, plus the acceptance, mapped to its own localized key.</summary>
    public string MessageKey => Accepted ? "BroadcastCastAccepted" : Reason switch
    {
        "declined" => "BroadcastCastDeclined",
        "timeout" => "BroadcastCastTimeout",
        "unsupported" => "BroadcastCastUnsupported",
        "unavailable" => "BroadcastCastUnavailable",
        _ => "BroadcastCastUnavailable"
    };

    public static ExchangeCastResult? TryParse(JsonElement root)
    {
        if (ExchangeProtocol.String(root, "type") != "cast-result")
        {
            return null;
        }

        var broadcastId = ExchangeProtocol.String(root, "broadcastId");
        var receiverDeviceId = ExchangeProtocol.String(root, "receiverDeviceId");
        if (string.IsNullOrWhiteSpace(broadcastId) || broadcastId.Length > MaximumIdLength
            || string.IsNullOrWhiteSpace(receiverDeviceId) || receiverDeviceId.Length > MaximumIdLength)
        {
            return null;
        }

        var accepted = root.TryGetProperty("accepted", out var value) && value.ValueKind == JsonValueKind.True;
        var reason = ExchangeProtocol.String(root, "reason") ?? "unavailable";
        return new ExchangeCastResult(broadcastId, receiverDeviceId, accepted, reason);
    }
}

/// <summary>
/// SP-0217: the tunnel request of `DEVICE-EXCHANGE` 7.5 - `open` on this device's control stream, with
/// `tunnelId` and the `broadcastId` plus `scheme` it is for. The producer opens its own data stream,
/// sends `hello` then `attach {tunnelId}`, and bridges to the broadcast's local service.
/// </summary>
public sealed record ExchangeTunnelOpen(string TunnelId, string BroadcastId, string Scheme)
{
    private const int MaximumIdLength = 128;
    private const int MaximumTunnelsPerBroadcast = 16;

    /// <summary>Item L: at most 16 tunnels per resource; the producer holds the same bound.</summary>
    public static int TunnelsPerBroadcast => MaximumTunnelsPerBroadcast;

    public static ExchangeTunnelOpen? TryParse(JsonElement root)
    {
        if (ExchangeProtocol.String(root, "type") != "open")
        {
            return null;
        }

        var tunnelId = ExchangeProtocol.String(root, "tunnelId");
        var broadcastId = ExchangeProtocol.String(root, "broadcastId");
        var scheme = ExchangeProtocol.String(root, "scheme") ?? BroadcastDescriptorWriter.HttpTunnelScheme;
        if (string.IsNullOrWhiteSpace(tunnelId) || tunnelId.Length > MaximumIdLength
            || string.IsNullOrWhiteSpace(broadcastId) || broadcastId.Length > MaximumIdLength)
        {
            return null;
        }

        return new ExchangeTunnelOpen(tunnelId, broadcastId, scheme);
    }
}

/// <summary>
/// SP-0217 requirement 5 / `LIVE-BROADCAST` item F: a cast goes only to a receiver whose declared
/// pairs cover one of the broadcast's endpoints - so a receiver that cannot play it is never sent an
/// offer. The judgment is here, in Core, so the UI that greys a receiver out and the wire that would
/// send the offer cannot disagree.
/// </summary>
public static class ExchangeCastEligibility
{
    /// <summary>
    /// Whether a cast may be offered to a receiver (items E and CJ): its declaration must cover an endpoint, and a device
    /// that declared nothing usable (<paramref name="receiverPlays"/> null) has covered none, so it is not offered one - the
    /// server would only answer <c>unsupported</c>, and the person would have spent an offer on a device that cannot play.
    /// </summary>
    public static bool CanOffer(IReadOnlyList<ExchangePlayPair>? receiverPlays, IReadOnlyList<FastMediaSorterBroadcastEndpoint> endpoints) =>
        receiverPlays is not null && CanPlay(receiverPlays, endpoints);

    public static bool CanPlay(IReadOnlyList<ExchangePlayPair> receiverPlays, IReadOnlyList<FastMediaSorterBroadcastEndpoint> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            if (endpoint.Transport is null || endpoint.Mode is null)
            {
                continue;
            }

            if (receiverPlays.Any(pair =>
                    string.Equals(pair.Mode, endpoint.Mode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(pair.Transport, endpoint.Transport, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return endpoints.Count == 0;
    }
}
