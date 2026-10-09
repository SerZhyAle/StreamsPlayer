using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// A cast-offer received over the exchange control stream (DEVICE-EXCHANGE 7.8, SP-0205).
/// </summary>
/// <remarks>
/// <para>Section 7.8 defines the offer as <c>broadcast</c> (the section 6.3 record, whose
/// <c>descriptor</c> is the <c>LIVE-BROADCAST</c> descriptor) plus <c>fromDeviceId</c>, and the answer as
/// <c>broadcastId</c>, <c>accepted</c>, <c>reason</c>. The broadcast id is therefore the offer's identity;
/// <see cref="CastId"/> is only an optional extra a sender may add, echoed back when present.</para>
/// </remarks>
public sealed record ExchangeCastOffer(
    string BroadcastId,
    string DeviceId,
    string? DeviceName,
    string Title,
    FastMediaSorterBroadcast? Descriptor,
    ExchangeBroadcastSupport Support,
    string? CastId = null)
{
    public static bool TryParse(JsonElement root, out ExchangeCastOffer? offer)
    {
        offer = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = ExchangeProtocol.String(root, "type");
        if (type != "cast-offer")
        {
            return false;
        }

        // Without the record there is no broadcast to answer for; the contract's offer always carries it.
        if (!root.TryGetProperty("broadcast", out var record) || record.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var broadcastId = ExchangeProtocol.String(record, "broadcastId");
        if (string.IsNullOrWhiteSpace(broadcastId))
        {
            return false;
        }

        var castId = ExchangeProtocol.String(root, "castId") ?? ExchangeProtocol.String(root, "offerId");
        var deviceId = ExchangeProtocol.String(root, "fromDeviceId")
            ?? ExchangeProtocol.String(record, "deviceId")
            ?? "";
        var deviceName = ExchangeProtocol.String(root, "fromDeviceName");

        FastMediaSorterBroadcast? descriptor = null;
        var support = ExchangeBroadcastSupport.Unsupported;
        var title = ExchangeProtocol.String(record, "title");

        if (record.TryGetProperty("descriptor", out var descElement) && descElement.ValueKind == JsonValueKind.Object)
        {
            var read = FastMediaSorterBroadcastDescriptor.Read(descElement.GetRawText());
            if (read.IsAccepted && read.Broadcast is not null)
            {
                descriptor = read.Broadcast;
                support = ExchangeDirectoryState.SupportOf(read.Broadcast);
                title ??= read.Broadcast.Title;
            }
            else if (read.Status == FastMediaSorterBroadcastReadStatus.UnsupportedSchema)
            {
                support = ExchangeBroadcastSupport.UnsupportedSchema;
            }
        }

        title ??= "Live Broadcast";
        offer = new ExchangeCastOffer(broadcastId, deviceId, deviceName, title, descriptor, support, castId);
        return true;
    }
}

/// <summary>
/// Helper to construct cast-answer frames for the exchange control stream (DEVICE-EXCHANGE 7.8).
/// </summary>
/// <remarks>
/// The answer is keyed by <c>broadcastId</c>. A <c>castId</c> the offer carried as an optional extra is echoed
/// back so a sender that correlates on it still can, but it is never the identity.
/// </remarks>
public static class ExchangeCastAnswer
{
    public static object Accept(string broadcastId, string? castId = null) =>
        Build(broadcastId, castId, accepted: true, reason: null);

    public static object Decline(string broadcastId, string reason = "declined", string? castId = null) =>
        Build(broadcastId, castId, accepted: false, reason);

    public static object Unsupported(string broadcastId, string? castId = null) =>
        Decline(broadcastId, "unsupported", castId);

    /// <summary>DEVICE-EXCHANGE item R: the reason when no answer was given inside the offer window.</summary>
    public static object Timeout(string broadcastId, string? castId = null) =>
        Decline(broadcastId, "timeout", castId);

    /// <summary>The frame that answers <paramref name="offer"/> with what the user or the clock decided.</summary>
    public static object For(ExchangeCastOffer offer, ExchangeCastDecision decision)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return decision switch
        {
            ExchangeCastDecision.Accepted => Accept(offer.BroadcastId, offer.CastId),
            ExchangeCastDecision.TimedOut => Timeout(offer.BroadcastId, offer.CastId),
            _ => Decline(offer.BroadcastId, castId: offer.CastId)
        };
    }

    private static Dictionary<string, object> Build(string broadcastId, string? castId, bool accepted, string? reason)
    {
        var frame = new Dictionary<string, object>
        {
            ["schemaVersion"] = 2,
            ["type"] = "cast-answer",
            ["broadcastId"] = broadcastId
        };
        if (!string.IsNullOrWhiteSpace(castId))
        {
            frame["castId"] = castId;
        }

        frame["accepted"] = accepted;
        if (reason is not null)
        {
            frame["reason"] = reason;
        }

        return frame;
    }
}

/// <summary>
/// A cast-stop notification received over the exchange control stream (DEVICE-EXCHANGE 7.8, SP-0205).
/// </summary>
public sealed record ExchangeCastStop(string? CastId, string? BroadcastId)
{
    public static bool TryParse(JsonElement root, out ExchangeCastStop? stop)
    {
        stop = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = ExchangeProtocol.String(root, "type");
        if (type != "cast-stop")
        {
            return false;
        }

        var castId = ExchangeProtocol.String(root, "castId") ?? ExchangeProtocol.String(root, "offerId");
        var broadcastId = ExchangeProtocol.String(root, "broadcastId");

        stop = new ExchangeCastStop(castId, broadcastId);
        return true;
    }
}
