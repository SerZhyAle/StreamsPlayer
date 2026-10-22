using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// The sealed-content corpus of DEVICE-EXCHANGE section 27 (`vectors/e2e-v1.json`) read once for the suites
/// that replay it: the devices with their keys, the boxes, the failing cases and the relay stream.
/// </summary>
internal static class E2eCorpus
{
    internal static string Directory => Path.Combine(AppContext.BaseDirectory, "Vectors", "e2e-v1");

    internal static JsonDocument Load(string fileName = "e2e-v1.json") =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Directory, fileName)));

    internal sealed record Device(string Name, string DeviceId, ExchangeE2eKeyPair Pair, string CheckCode);

    internal static IReadOnlyDictionary<string, Device> Devices(JsonElement root)
    {
        var devices = new Dictionary<string, Device>();
        foreach (var entry in root.GetProperty("devices").EnumerateArray())
        {
            Assert.True(ExchangeBase64Url.TryDecode(entry.GetProperty("e2eKey").GetProperty("kem").GetString(), out var point));
            var pair = ExchangeE2eKeyPair.TryImport(Convert.FromHexString(entry.GetProperty("privateKey").GetString()!), point);
            Assert.NotNull(pair);
            var name = entry.GetProperty("name").GetString()!;
            devices[name] = new Device(name, entry.GetProperty("deviceId").GetString()!, pair, entry.GetProperty("checkCode").GetString()!);
        }

        return devices;
    }

    /// <summary>Order-insensitive structural equality of two JSON values.</summary>
    internal static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var leftProperties = left.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var rightProperties = right.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                return leftProperties.Count == rightProperties.Count
                    && leftProperties.All(p => rightProperties.TryGetValue(p.Key, out var other) && JsonEquals(p.Value, other));
            case JsonValueKind.Array:
                var leftItems = left.EnumerateArray().ToArray();
                var rightItems = right.EnumerateArray().ToArray();
                return leftItems.Length == rightItems.Length && leftItems.Zip(rightItems).All(pair => JsonEquals(pair.First, pair.Second));
            case JsonValueKind.String:
                return left.GetString() == right.GetString();
            default:
                return left.GetRawText() == right.GetRawText();
        }
    }
}
