using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// DEVICE-EXCHANGE item AM, the bounds of an `enroll`, as a checker over a parsed frame: what a conforming server
/// closes the stream over without an answer. It is held against the real server twice - every enroll the corpora
/// carry must pass it and the one corpus case that the server closed (`device-receiver-too-many-modes`) must not -
/// before it is held against this product's own enroll.
/// </summary>
internal static class ExchangeEnrollmentBounds
{
    private const int MaximumEntries = 8;
    private const int MaximumPairs = 8;

    /// <summary>The item's violations of <paramref name="enroll"/>; empty when the frame is inside the bounds. Absent members are fine.</summary>
    internal static IReadOnlyList<string> Violations(JsonElement enroll)
    {
        var found = new List<string>();
        if (enroll.TryGetProperty("receiver", out var receiver) && receiver.ValueKind == JsonValueKind.Object)
        {
            var modes = Entries(receiver, "modes", 16, "receiver.modes", found);
            var transports = Entries(receiver, "transports", 16, "receiver.transports", found);
            if (receiver.TryGetProperty("plays", out var plays) && plays.ValueKind == JsonValueKind.Array)
            {
                if (plays.GetArrayLength() > MaximumEntries)
                {
                    found.Add("receiver.plays has " + plays.GetArrayLength() + " entries");
                }

                foreach (var pair in plays.EnumerateArray())
                {
                    foreach (var member in new[] { "mode", "transport" })
                    {
                        Length(pair, member, 16, "receiver.plays." + member, found);
                    }
                }

                // Item E reads the two lists as their product, so a declaration over the bound omits `plays`.
                if (modes * transports > MaximumPairs)
                {
                    found.Add("receiver.plays is present although modes x transports is " + (modes * transports));
                }
            }
        }

        Entries(enroll, "roles", 32, "roles", found);
        Entries(enroll, "capabilities", 32, "capabilities", found);
        Length(enroll, "login", 256, "login", found);
        Length(enroll, "pairingCode", 64, "pairingCode", found);
        Length(enroll, "deviceName", 64, "deviceName", found);
        Length(enroll, "product", 32, "product", found);
        Length(enroll, "platform", 32, "platform", found);
        Length(enroll, "productVersion", 64, "productVersion", found);
        if (ExchangeProtocol.String(enroll, "password") is { } password && Encoding.UTF8.GetByteCount(password) > 1024)
        {
            found.Add("password is longer than 1024 bytes");
        }

        if (ExchangeProtocol.String(enroll, "deviceId") is { } deviceId
            && (deviceId.Length != 22 || !ExchangeBase64Url.TryDecode(deviceId, out var decoded) || decoded.Length != 16))
        {
            found.Add("deviceId is not 22 characters of base64url that decode to 16 bytes");
        }

        return found;
    }

    private static int Entries(JsonElement container, string member, int entryLength, string path, List<string> found)
    {
        if (!container.TryGetProperty(member, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        if (list.GetArrayLength() > MaximumEntries)
        {
            found.Add(path + " has " + list.GetArrayLength() + " entries");
        }

        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && Invalid(entry.GetString()!, entryLength))
            {
                found.Add(path + " holds an entry over " + entryLength + " characters or with a control character");
            }
        }

        return list.GetArrayLength();
    }

    private static void Length(JsonElement container, string member, int maximum, string path, List<string> found)
    {
        if (ExchangeProtocol.String(container, member) is { } text && Invalid(text, maximum))
        {
            found.Add(path + " is over " + maximum + " characters or holds a control character");
        }
    }

    private static bool Invalid(string text, int maximum) => text.Length > maximum || text.Any(char.IsControl);
}
