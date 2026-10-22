using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0226: the sealed-content members of the directory (`DEVICE-EXCHANGE` section 27) - a device's
/// capability and public key (item DF), its `lastSeen`, and a broadcast record's box (item DK). Reading
/// them is all the directory does with them: it never opens, checks or alters a box.
/// </summary>
public sealed partial class ExchangeDirectoryState
{
    /// <summary>`capabilities` lists `e2e-v1`, and `e2eKey` is `{v: 1, kem}` with a 65-byte P-256 point; an unusable key reads as none.</summary>
    private static (bool HasCapability, byte[]? Key) ReadE2e(JsonElement device)
    {
        var hasCapability = ReadStrings(device, "capabilities").Contains("e2e-v1", StringComparer.Ordinal);
        if (!device.TryGetProperty("e2eKey", out var key) || key.ValueKind != JsonValueKind.Object
            || !key.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var v) || v != 1
            || ReadString(key, "kem") is not { } kem
            || !ExchangeBase64Url.TryDecode(kem, out var point)
            || point.Length != ExchangeE2eKeyPair.PublicLength || point[0] != 0x04)
        {
            return (hasCapability, null);
        }

        return (hasCapability, point);
    }

    private static DateTimeOffset? ReadLastSeen(JsonElement device) =>
        ReadString(device, "lastSeen") is { } stamp &&
        DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var lastSeen)
            ? lastSeen
            : null;

    /// <summary>The record's `sealed` member as a box; <paramref name="unreadable"/> when the member exists but is not one.</summary>
    internal static ExchangeSealedBox? ReadSealedBox(JsonElement record, out bool unreadable)
    {
        unreadable = false;
        if (!record.TryGetProperty("sealed", out var member))
        {
            return null;
        }

        var box = ExchangeSealedBox.TryRead(member);
        unreadable = box is null;
        return box;
    }
}

/// <summary>
/// Which devices a sender seals to, and what the person is told about the others (`DEVICE-EXCHANGE` item DG):
/// a recipient is a device of the entry that the program itself lists, with the capability and a trusted key.
/// </summary>
public static class ExchangeRecipients
{
    /// <summary>
    /// The sealed state of one listed device, observing its key on the way: the first key seen is trusted
    /// (and remembered in <paramref name="trust"/>), a different one later is `key-changed`, and a device
    /// with no capability or no usable key is `e2e-missing`. <see cref="ExchangeSealedState.None"/> means a
    /// device content may be sealed to.
    /// </summary>
    public static ExchangeSealedState Classify(ExchangeDeviceRecord device, ExchangeDeviceTrust trust)
    {
        if (!device.HasE2eCapability || device.E2eKey is null)
        {
            return ExchangeSealedState.E2eMissing;
        }

        return trust.ObserveDevice(device.DeviceId, device.E2eKey) == ExchangeKeyObservation.KeyChanged
            ? ExchangeSealedState.KeyChanged
            : ExchangeSealedState.None;
    }

    /// <summary>
    /// The recipients of a box from <paramref name="selfDeviceId"/>: every other listed device in a trusted state,
    /// most recently seen first (a box cuts to forty, so the order is the cut), online or not. The key used is the
    /// trusted one, never the one the directory just showed.
    /// </summary>
    public static IReadOnlyList<ExchangeSealedRecipient> For(
        IEnumerable<ExchangeDeviceRecord> devices, string selfDeviceId, ExchangeDeviceTrust trust)
    {
        var recipients = new List<(DateTimeOffset Seen, ExchangeSealedRecipient Recipient)>();
        foreach (var device in devices)
        {
            if (string.Equals(device.DeviceId, selfDeviceId, StringComparison.Ordinal)
                || Classify(device, trust) != ExchangeSealedState.None
                || trust.TrustedDevice(device.DeviceId) is not { } point)
            {
                continue;
            }

            recipients.Add((device.LastSeen ?? DateTimeOffset.MinValue, new ExchangeSealedRecipient(device.DeviceId, point)));
        }

        return recipients.OrderByDescending(entry => entry.Seen).Select(entry => entry.Recipient).ToArray();
    }
}
