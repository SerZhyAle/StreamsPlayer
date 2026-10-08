using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// A cast-offer received over the exchange control stream (DEVICE-EXCHANGE 7.8, SP-0205).
/// </summary>
public sealed record ExchangeCastOffer(
    string CastId,
    string BroadcastId,
    string DeviceId,
    string? DeviceName,
    string Title,
    FastMediaSorterBroadcast? Descriptor,
    ExchangeBroadcastSupport Support)
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

        var castId = ExchangeProtocol.String(root, "castId") ?? ExchangeProtocol.String(root, "offerId");
        if (string.IsNullOrWhiteSpace(castId))
        {
            return false;
        }

        var broadcastId = ExchangeProtocol.String(root, "broadcastId") ?? "";
        var deviceId = ExchangeProtocol.String(root, "fromDeviceId")
            ?? ExchangeProtocol.String(root, "deviceId")
            ?? ExchangeProtocol.String(root, "senderDeviceId")
            ?? "";
        var deviceName = ExchangeProtocol.String(root, "fromDeviceName")
            ?? ExchangeProtocol.String(root, "deviceName");

        FastMediaSorterBroadcast? descriptor = null;
        var support = ExchangeBroadcastSupport.Unsupported;
        var title = ExchangeProtocol.String(root, "title");

        if ((root.TryGetProperty("descriptor", out var descElement) || root.TryGetProperty("broadcast", out descElement))
            && descElement.ValueKind == JsonValueKind.Object)
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
        offer = new ExchangeCastOffer(castId, broadcastId, deviceId, deviceName, title, descriptor, support);
        return true;
    }
}

/// <summary>
/// Helper to construct cast-answer frames for the exchange control stream (DEVICE-EXCHANGE 7.8).
/// </summary>
public static class ExchangeCastAnswer
{
    public static object Accept(string castId) => new
    {
        schemaVersion = 2,
        type = "cast-answer",
        castId,
        accepted = true
    };

    public static object Decline(string castId, string reason = "declined") => new
    {
        schemaVersion = 2,
        type = "cast-answer",
        castId,
        accepted = false,
        reason
    };

    public static object Unsupported(string castId) => Decline(castId, "unsupported");

    public static object Timeout(string castId) => Decline(castId, "timeout");
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
