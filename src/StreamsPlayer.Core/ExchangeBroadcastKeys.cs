using System.Collections.Concurrent;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// What a sealed broadcast's box (or an outward link) hands the listener (`DEVICE-EXCHANGE` item DK): the relay
/// group key, the tunnel's inner-leaf pin and, for a protected broadcast, the pair. Secrets: held in memory, never
/// printed (the record prints no member), never logged.
/// </summary>
public sealed record ExchangeBroadcastSecrets(byte[]? RelayKey, string? TunnelPin, ExchangeRelayCredentials? Credentials)
{
    public override string ToString() => "ExchangeBroadcastSecrets { }";
}

/// <summary>What a playback leg does with one endpoint (`DEVICE-EXCHANGE` items DK and DP).</summary>
public sealed record ExchangeEndpointDecision(
    bool Allowed, ExchangeSealedState State, bool IsSealed, byte[]? RelayKey, string? TunnelPin, ExchangeRelayCredentials? Credentials)
{
    public static ExchangeEndpointDecision Clear { get; } = new(true, ExchangeSealedState.None, false, null, null, null);

    public static ExchangeEndpointDecision Refused(ExchangeSealedState state) => new(false, state, false, null, null, null);

    /// <summary>The reason string a failure path carries; the message for it is the table of <see cref="ExchangeBroadcastCertificateException.MessageKey"/>.</summary>
    public string Reason => State.Code();

    /// <summary>What the proxy needs to open this endpoint's relay, or null when the decision is not a sealed relay.</summary>
    public ExchangeSealedRelayOptions? RelayOptions(string relayUrl) =>
        IsSealed && RelayKey is { } key && ExchangeBroadcastKeys.BroadcastIdOf(relayUrl) is { } id
            ? new ExchangeSealedRelayOptions(id, key)
            : null;

    public override string ToString() => $"ExchangeEndpointDecision {{ Allowed = {Allowed}, State = {State}, IsSealed = {IsSealed} }}";
}

/// <summary>
/// The keys of the sealed broadcasts this PC can listen to, in memory only: filled when a directory record, a cast
/// offer or a guest's grant arrives (its box opened with this entry's key) or when a link is imported, and asked by
/// the playback legs, which hold an endpoint and nothing else. Keyed by the broadcast id that the relay and tunnel
/// addresses carry. A restart empties it; what must survive a restart (a link's keys) comes back through
/// <see cref="PersistedLookup"/>, which the application points at its secret store.
/// </summary>
public static class ExchangeBroadcastKeys
{
    private static readonly ConcurrentDictionary<string, ExchangeBroadcastSecrets> Held = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ExchangeSealedState> Failed = new(StringComparer.Ordinal);

    /// <summary>The application's secret store for the keys of a link a person imported; null in a process that has none.</summary>
    public static Func<string, ExchangeBroadcastSecrets?>? PersistedLookup { get; set; }

    public static void Put(string broadcastId, ExchangeBroadcastSecrets secrets)
    {
        Held[broadcastId] = secrets;
        Failed.TryRemove(broadcastId, out _);
    }

    public static void Fail(string broadcastId, ExchangeSealedState state)
    {
        Held.TryRemove(broadcastId, out _);
        Failed[broadcastId] = state;
    }

    public static ExchangeBroadcastSecrets? Find(string broadcastId) =>
        Held.TryGetValue(broadcastId, out var secrets) ? secrets : PersistedLookup?.Invoke(broadcastId);

    /// <summary>The state a record's box ended in when it could not be opened; none while it has not been tried or opened.</summary>
    public static ExchangeSealedState FailureOf(string broadcastId) =>
        Failed.TryGetValue(broadcastId, out var state) ? state : ExchangeSealedState.None;

    /// <summary>
    /// A sealed link the person imported: its endpoints carry the group key (RELAY) and the pin (TUNNEL) that open the
    /// broadcast. They are kept for this process and handed to <paramref name="persist"/> (the application's secret
    /// store) so a restart does not lose them; the channel itself keeps neither.
    /// </summary>
    public static void RememberLink(IEnumerable<FastMediaSorterBroadcastEndpoint> endpoints, Action<string, ExchangeBroadcastSecrets>? persist)
    {
        foreach (var group in endpoints.Where(endpoint => endpoint.Seal is { IsKnown: true }).GroupBy(endpoint => BroadcastIdOf(endpoint.Url)))
        {
            if (group.Key is not { } id)
            {
                continue;
            }

            var relayKey = group.Select(endpoint => endpoint.Seal!.GroupKey()).FirstOrDefault(key => key is not null);
            var pin = group.Select(endpoint => endpoint.Seal!.Pin).FirstOrDefault(value => value is not null);
            if (relayKey is null && pin is null)
            {
                continue;
            }

            var held = Held.TryGetValue(id, out var existing) ? existing : null;
            var merged = new ExchangeBroadcastSecrets(relayKey ?? held?.RelayKey, pin ?? held?.TunnelPin, held?.Credentials);
            Put(id, merged);
            persist?.Invoke(id, merged);
        }
    }

    public static void Clear()
    {
        Held.Clear();
        Failed.Clear();
    }

    /// <summary>The broadcast id inside a relay listen address (`/v2/b/&lt;id&gt;/stream`) or a tunnel address (`fmsx://host/b/&lt;id&gt;/&lt;scheme&gt;`).</summary>
    public static string? BroadcastIdOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(segments, "b");
        return index >= 0 && index + 1 < segments.Length && segments[index + 1].Length is > 0 and <= 128
            ? Uri.UnescapeDataString(segments[index + 1])
            : null;
    }

    /// <summary>
    /// Opens the box of a directory record (or of a guest's granted record, when <see cref="ExchangeBroadcastRecord.GrantId"/> is set)
    /// with this entry's key and keeps what is in it. Returns the state the person is shown: none when it opened or
    /// when the record has no box (then it is the endpoints that decide), otherwise why it did not.
    /// </summary>
    public static ExchangeSealedState OpenRecord(
        ExchangeBroadcastRecord record, string selfDeviceId, ExchangeE2eKeyPair self, ExchangeDeviceTrust trust)
    {
        if (record.Sealed is not { } box)
        {
            if (record.SealedUnreadable)
            {
                Fail(record.BroadcastId, ExchangeSealedState.SealedFailed);
                return ExchangeSealedState.SealedFailed;
            }

            return ExchangeSealedState.None;
        }

        string context;
        byte[] trusted;
        if (record.GrantId is { Length: > 0 } grantId)
        {
            // Item DJ: a guest knows none of the owner's devices; the first box of a grant fixes the sender's key.
            context = ExchangeSealedContext.Grant(grantId);
            if (!ExchangeBase64Url.TryDecode(box.FromKey, out var fromKey) || fromKey.Length != ExchangeE2eKeyPair.PublicLength
                || fromKey[0] != 0x04)
            {
                return Failed_(record.BroadcastId, ExchangeSealedState.SealedFailed);
            }

            if (trust.ObserveGrant(grantId, fromKey) == ExchangeKeyObservation.KeyChanged)
            {
                return Failed_(record.BroadcastId, ExchangeSealedState.KeyChanged);
            }

            trusted = fromKey;
        }
        else
        {
            context = ExchangeSealedContext.Broadcast(record.BroadcastId);
            if (trust.TrustedDevice(box.From) is not { } known)
            {
                // The sender is not a device this program lists, so it is nobody it has a key for (item DG).
                return Failed_(record.BroadcastId, ExchangeSealedState.SealedFailed);
            }

            trusted = known;
        }

        var opening = ExchangeSealedBoxes.Open(box, context, selfDeviceId, self, trusted);
        if (!opening.Opened)
        {
            // A sender whose key moved under a known id is the key-changed device, not an unreadable box.
            var state = opening.Failure == ExchangeSealedFailure.UntrustedSender && record.GrantId is null
                ? ExchangeSealedState.KeyChanged
                : ExchangeSealedState.SealedFailed;
            return Failed_(record.BroadcastId, state);
        }

        var secrets = ReadPayload(opening.Payload!);
        if (secrets is null)
        {
            return Failed_(record.BroadcastId, ExchangeSealedState.SealedFailed);
        }

        Put(record.BroadcastId, secrets);
        return ExchangeSealedState.None;
    }

    private static ExchangeSealedState Failed_(string broadcastId, ExchangeSealedState state)
    {
        Fail(broadcastId, state);
        return state;
    }

    /// <summary>The payload of item DK's box: `relayKey`, `tunnelPin`, `credentials`, each optional; a member that is present and wrong spoils the box.</summary>
    internal static ExchangeBroadcastSecrets? ReadPayload(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            byte[]? relayKey = null;
            if (root.TryGetProperty("relayKey", out var keyElement))
            {
                if (keyElement.ValueKind != JsonValueKind.String
                    || !ExchangeBase64Url.TryDecode(keyElement.GetString(), out relayKey!)
                    || relayKey.Length != ExchangeSealedRelayRecords.GroupKeyLength)
                {
                    return null;
                }
            }

            string? pin = null;
            if (root.TryGetProperty("tunnelPin", out var pinElement))
            {
                pin = pinElement.ValueKind == JsonValueKind.String ? pinElement.GetString() : null;
                if (pin is null || !pin.StartsWith("SHA256:", StringComparison.Ordinal))
                {
                    return null;
                }
            }

            ExchangeRelayCredentials? credentials = null;
            if (root.TryGetProperty("credentials", out var pair) && pair.ValueKind == JsonValueKind.Object)
            {
                // Item BF spells the member `user`; the sealed corpus's stand-in payload spells it `username`. Both read.
                credentials = ExchangeRelayCredentials.Create(
                    Text(pair, "user") ?? Text(pair, "username"), Text(pair, "password"));
            }

            return new ExchangeBroadcastSecrets(relayKey, pin, credentials);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// The one rule every playback leg applies before it opens an endpoint: a LAN endpoint is not judged; an exchange
    /// endpoint plays only sealed, with the keys the endpoint itself carries (an outward link) or the ones its box
    /// handed over - and otherwise is refused with the state the person is told, never played as clear bytes.
    /// </summary>
    public static ExchangeEndpointDecision Decide(FastMediaSorterBroadcastEndpoint endpoint)
    {
        if (!ExchangeEndpointKinds.IsExchangeEndpoint(endpoint))
        {
            return ExchangeEndpointDecision.Clear;
        }

        if (endpoint.Seal is not { IsKnown: true } seal)
        {
            return ExchangeEndpointDecision.Refused(ExchangeSealedState.E2eMissing);
        }

        var broadcastId = BroadcastIdOf(endpoint.Url);
        var held = broadcastId is null ? null : Find(broadcastId);
        var relayKey = seal.GroupKey() ?? held?.RelayKey;
        var pin = seal.Pin ?? held?.TunnelPin;
        var isTunnel = string.Equals(endpoint.Transport, "TUNNEL", StringComparison.OrdinalIgnoreCase)
            || endpoint.Url.StartsWith("fmsx:", StringComparison.OrdinalIgnoreCase);
        if (isTunnel ? pin is null : relayKey is null)
        {
            var failure = broadcastId is null ? ExchangeSealedState.None : FailureOf(broadcastId);
            return ExchangeEndpointDecision.Refused(failure == ExchangeSealedState.None ? ExchangeSealedState.SealedFailed : failure);
        }

        return new ExchangeEndpointDecision(true, ExchangeSealedState.None, true, relayKey, pin, held?.Credentials);
    }
}
