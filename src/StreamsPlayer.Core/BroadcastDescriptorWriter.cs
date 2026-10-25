using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// How the exchange endpoints of a description are sealed (DEVICE-EXCHANGE item DK, LIVE-BROADCAST item Q).
/// </summary>
public enum BroadcastSealMode
{
    /// <summary>An older producer's shape: exchange endpoints with no seal. Never written by a build that seals.</summary>
    None,

    /// <summary>The directory record: each RELAY and TUNNEL endpoint declares seal: {v} and no key - the keys ride in the record's box.</summary>
    Record,

    /// <summary>
    /// The description a person hands out (a link, a file, a barcode): RELAY carries seal: {v, key} and TUNNEL
    /// seal: {v, pin}. An endpoint whose key or pin is missing is left out, because nobody holding only that description
    /// could play it. The keys make this text a secret: it is handed by the person and never goes through the server.
    /// </summary>
    Outward
}

/// <summary>
/// What one broadcast's description is built from: the endpoints this broadcast actually has. The
/// producer's own facts, not a copy of the wire - the writer owns every address shape and every
/// latency number, so no call site can publish a wrong one.
/// </summary>
public sealed record BroadcastWirePlan(
    string BroadcastId,
    string DeviceId,
    string Title,
    BroadcastSource Source,
    string? LanAddress,
    int LanPort,
    string? OpaqueToken,
    string? PublicEndpoint,
    bool Relay,
    bool Tunnel,
    string? CertFingerprint,
    long SampleRate,
    int Channels,
    long BitrateKbps,
    long? VideoBitrateKbps = null,
    BroadcastSealMode Seal = BroadcastSealMode.None,
    /// <summary>The link's group key (base64url), written into an outward RELAY endpoint; a secret.</summary>
    string? LinkGroupKey = null,
    /// <summary>The inner tunnel leaf's SHA256: pin, written into an outward TUNNEL endpoint.</summary>
    string? TunnelPin = null,
    bool? HasAudio = null,
    /// <summary>LIVE-BROADCAST item M: the user an outsider answers the password challenge with; null writes no `auth` (item R: the person chose none, or a LAN-only broadcast).</summary>
    string? AuthUser = null)
{
    public string SourceId => BroadcastSources.SourceId(DeviceId, Source);
    public string Mode => !IsVideo ? FastMediaSorterBroadcastDescriptor.AudioOnlyMode
        : (HasAudio ?? Source == BroadcastSource.Camera) ? FastMediaSorterBroadcastDescriptor.VideoAudioMode
        : FastMediaSorterBroadcastDescriptor.VideoOnlyMode;
    public bool IsVideo => !BroadcastSources.IsAudioOnly(Source);
}

/// <summary>
/// SP-0217: the one writer of this product's own `LIVE-BROADCAST` descriptors (the reader is
/// `FastMediaSorterBroadcastDescriptor`; this is its encode-side mirror) and of the endpoint shapes of
/// `DEVICE-EXCHANGE` section 7.7 and `LIVE-BROADCAST` amendment items C, D, H, J.
/// </summary>
/// <remarks>
/// <para>The rules this writer guarantees, whatever it is fed: endpoints in the producer's order (LAN
/// `HTTP`, then `RELAY`, then `TUNNEL`); never a loopback or unspecified address, in `inner` included;
/// `inner` never omitted - without a LAN address it keeps the scheme and the path on the documentation
/// host `192.0.2.1` (item J); a video LAN URL carries the opaque token segment (item H);
/// `certFingerprint` only where the caller says the server's leaf was pinned by its fingerprint;
/// `targetLatencyMs` per transport (item H for the LAN video shape, item D for the exchange shapes).</para>
/// <para>One writer behind every hand-off channel: a link, an import file and the account directory all
/// carry the descriptor this writer produced, so no channel can diverge from another.</para>
/// </remarks>
public static class BroadcastDescriptorWriter
{
    /// <summary>Item J: the documentation host of RFC 5737, never routed, never a published address.</summary>
    public const string DocumentationHost = "192.0.2.1";

    public const int LanAudioTargetLatencyMs = 500;
    public const int LanVideoTargetLatencyMs = 1000;
    public const int ExchangeTargetLatencyMs = 2000;

    public const string AudioLanPath = "live-audio.aac";
    public const string AudioLanPathAlias = "live-audio";
    public const string VideoLanFileName = "live.ts";
    public const string AudioContentType = "audio/aac";
    public const string VideoContentType = "video/mp2t";
    public const string HttpTunnelScheme = "http";

    /// <summary>The LAN URL of this broadcast's HTTP shape - the address a consumer plays and never builds.</summary>
    public static string LanPath(BroadcastSource source, string? opaqueToken) =>
        BroadcastSources.IsAudioOnly(source) ? "/" + AudioLanPath : $"/{opaqueToken}/{VideoLanFileName}";

    /// <summary>The LAN URL the tunnel bridges to; with no LAN address the documentation host carries the scheme and the path (item J).</summary>
    public static string InnerUrl(BroadcastWirePlan plan) =>
        "http://" + (plan.LanAddress ?? DocumentationHost) + ":" + plan.LanPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + LanPath(plan.Source, plan.OpaqueToken);

    /// <summary>Writes the descriptor as one UTF-8 JSON object, `schemaVersion` 1, in the section 3.1/3.2 shape.</summary>
    /// <exception cref="ArgumentException">A LAN address that is loopback or unspecified, a video source
    /// without its opaque token, an exchange endpoint without a public endpoint, or an unsupported mode -
    /// the caller owns none of these shapes, so each is a bug in the plan, not a runtime condition.</exception>
    public static string Write(BroadcastWirePlan plan)
    {
        if (!FastMediaSorterBroadcastDescriptor.IsSupportedMode(plan.Mode))
        {
            throw new ArgumentException("Unsupported broadcast mode.", nameof(plan));
        }

        string? lanUrl = null;
        if (plan.LanAddress is { } lan)
        {
            if (!IPAddress.TryParse(lan, out var address) || address.AddressFamily != AddressFamily.InterNetwork
                || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any))
            {
                // Producer rule 1 (item D): a loopback address is never written anywhere; 0.0.0.0 is not
                // an address a consumer can reach either.
                throw new ArgumentException("The LAN address of a broadcast must be a reachable IPv4 address.", nameof(plan));
            }

            if (plan.IsVideo && string.IsNullOrWhiteSpace(plan.OpaqueToken))
            {
                // SP-0217 constraint 4 / item H: a screen or camera on the LAN carries the opaque segment.
                throw new ArgumentException("A video broadcast on the LAN requires its opaque path token.", nameof(plan));
            }

            lanUrl = "http://" + lan + ":" + plan.LanPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + LanPath(plan.Source, plan.OpaqueToken);
        }

        if ((plan.Relay || plan.Tunnel) && string.IsNullOrWhiteSpace(plan.PublicEndpoint))
        {
            throw new ArgumentException("An exchange endpoint requires the server's public endpoint.", nameof(plan));
        }

        var builder = new StringBuilder();
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", FastMediaSorterBroadcastDescriptor.SupportedSchemaVersion);
                // Top level: the first endpoint a legacy consumer can play as a plain address - the LAN
                // shape, else the relay listen, a plain HTTPS stream for any player.
                writer.WriteString("url", lanUrl ?? RelayUrl(plan) ?? TunnelUrl(plan));
                writer.WriteString("mode", plan.Mode);
                writer.WriteString("title", plan.Title);
                writer.WriteString("sourceId", plan.SourceId);
                writer.WriteBoolean("isLive", true);
                writer.WriteNumber("targetLatencyMs", plan.IsVideo ? LanVideoTargetLatencyMs : LanAudioTargetLatencyMs);
                if (plan.AuthUser is { Length: > 0 } authUser)
                {
                    // A marker with a user name and no secret: the password never travels in a description (item M).
                    writer.WriteStartObject("auth");
                    writer.WriteString("scheme", ExchangeBroadcastAuth.SupportedScheme);
                    writer.WriteString("user", authUser);
                    writer.WriteEndObject();
                }

                writer.WriteStartArray("endpoints");
                if (lanUrl is not null)
                {
                    WriteEndpoint(writer, lanUrl, "HTTP", plan, plan.IsVideo ? LanVideoTargetLatencyMs : LanAudioTargetLatencyMs, certFingerprint: null, inner: null);
                }

                var outward = plan.Seal == BroadcastSealMode.Outward;
                if (plan.Relay && (!outward || plan.LinkGroupKey is not null))
                {
                    WriteEndpoint(writer, RelayUrl(plan)!, "RELAY", plan, ExchangeTargetLatencyMs, plan.CertFingerprint, inner: null);
                }

                if (plan.Tunnel && (!outward || plan.TunnelPin is not null))
                {
                    WriteEndpoint(writer, TunnelUrl(plan)!, "TUNNEL", plan, ExchangeTargetLatencyMs, plan.CertFingerprint, InnerUrl(plan));
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            builder.Append(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        return builder.ToString();
    }


    /// <summary>
    /// The section 6.3 record of `broadcast-start` and `broadcast-update`: the descriptor verbatim inside it and, for a
    /// sealed broadcast, the box that carries its keys (`DEVICE-EXCHANGE` item DK). Every update repeats the box, because
    /// a record without it would read as a broadcast that is no longer sealed.
    /// </summary>
    public static Dictionary<string, object?> BroadcastRecord(
        BroadcastWirePlan plan, string descriptorJson, DateTimeOffset startedAt, ExchangeSealedBox? sealedBox = null)
    {
        var stamp = startedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var record = new Dictionary<string, object?>
        {
            ["broadcastId"] = plan.BroadcastId,
            ["deviceId"] = plan.DeviceId,
            ["title"] = plan.Title,
            ["mode"] = plan.Mode,
            ["startedAt"] = stamp,
            ["updatedAt"] = stamp,
            ["descriptor"] = JsonSerializer.Deserialize<JsonElement>(descriptorJson, ExchangeProtocol.JsonOptions)
        };
        if (sealedBox is not null)
        {
            record["sealed"] = sealedBox;
        }

        return record;
    }

    /// <summary>The `seal` member of an exchange endpoint, by the mode the description is written in; LAN endpoints have none.</summary>
    private static void WriteSeal(Utf8JsonWriter writer, string transport, BroadcastWirePlan plan)
    {
        if (plan.Seal == BroadcastSealMode.None || transport is not ("RELAY" or "TUNNEL"))
        {
            return;
        }

        writer.WriteStartObject("seal");
        writer.WriteNumber("v", ExchangeEndpointSeal.CurrentVersion);
        if (plan.Seal == BroadcastSealMode.Outward)
        {
            if (transport == "RELAY")
            {
                writer.WriteString("key", plan.LinkGroupKey);
            }
            else
            {
                writer.WriteString("pin", plan.TunnelPin);
            }
        }

        writer.WriteEndObject();
    }
    public static string RelayUrl(BroadcastWirePlan plan) => plan.PublicEndpoint is null ? "" :
        "https://" + plan.PublicEndpoint.TrimEnd('/') + "/v2/b/" + plan.BroadcastId + "/stream";

    public static string TunnelUrl(BroadcastWirePlan plan) => plan.PublicEndpoint is null ? "" :
        "fmsx://" + plan.PublicEndpoint.TrimEnd('/') + "/b/" + plan.BroadcastId + "/" + HttpTunnelScheme;

    private static void WriteEndpoint(Utf8JsonWriter writer, string url, string transport, BroadcastWirePlan plan,
        int targetLatencyMs, string? certFingerprint, string? inner)
    {
        writer.WriteStartObject();
        writer.WriteString("url", url);
        writer.WriteString("transport", transport);
        writer.WriteString("mode", plan.Mode);
        writer.WriteBoolean("isLive", true);
        writer.WriteNumber("targetLatencyMs", targetLatencyMs);
        if (plan.IsVideo)
        {
            writer.WriteString("videoCodec", "h264");
            writer.WriteNumber("keyframeIntervalMs", 2000);
            writer.WriteNumber("bitrate", plan.VideoBitrateKbps.GetValueOrDefault(2000) * 1000);
        }
        if (plan.Mode != FastMediaSorterBroadcastDescriptor.VideoOnlyMode)
        {
            writer.WriteString("audioCodec", "aac");
            writer.WriteNumber("sampleRate", plan.SampleRate);
            writer.WriteNumber("channels", plan.Channels);
            if (!plan.IsVideo) writer.WriteNumber("bitrate", plan.BitrateKbps * 1000);
        }

        if (certFingerprint is not null)
        {
            writer.WriteString("certFingerprint", certFingerprint);
        }

        if (inner is not null)
        {
            writer.WriteString("inner", inner);
        }

        WriteSeal(writer, transport, plan);
        writer.WriteEndObject();
    }
}
