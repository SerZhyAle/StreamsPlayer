using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamsPlayer.Core;

/// <summary>
/// The `seal` member of a `RELAY` or `TUNNEL` endpoint (`LIVE-BROADCAST` item Q, `DEVICE-EXCHANGE` item DK).
/// A directory record carries `{v}` alone - the keys ride in the record's box. A descriptor the person hands
/// outside the account carries the keys in the endpoint: `key` (the link's group key, `RELAY`) or `pin` (the
/// `SHA256:` pin of the tunnel's inner leaf, `TUNNEL`). Those are secrets: they never persist with the
/// channel (the properties are not serialized), never print, and are kept apart in the secret store.
/// </summary>
public sealed record ExchangeEndpointSeal(
    int V,
    [property: JsonIgnore] string? Key = null,
    [property: JsonIgnore] string? Pin = null)
{
    public const int CurrentVersion = 1;

    /// <summary>A version this build reads. Any other is skipped and never played as clear bytes (item DK).</summary>
    [JsonIgnore]
    public bool IsKnown => V == CurrentVersion;

    /// <summary>The 32-byte group key of an outward link, or null when absent or malformed.</summary>
    public byte[]? GroupKey() =>
        ExchangeBase64Url.TryDecode(Key, out var key) && key.Length == ExchangeSealedRelayRecords.GroupKeyLength ? key : null;

    /// <summary>Reads the member leniently: an object with an integer `v`; anything else is version 0, which nothing knows.</summary>
    public static ExchangeEndpointSeal? Read(JsonElement endpoint)
    {
        if (!endpoint.TryGetProperty("seal", out var seal))
        {
            return null;
        }

        if (seal.ValueKind != JsonValueKind.Object || !seal.TryGetProperty("v", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v))
        {
            return new ExchangeEndpointSeal(0);
        }

        return new ExchangeEndpointSeal(v, Text(seal, "key"), Text(seal, "pin"));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // The record would print its members, keys included; a log line or an exception text must not.
    public override string ToString() => $"ExchangeEndpointSeal {{ V = {V} }}";
}
