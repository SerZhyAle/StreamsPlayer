using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace StreamsPlayer.Core;

public sealed record ExchangePlayPair(string Mode, string Transport);

public static class ExchangeCapabilities
{
    /// <summary>
    /// SP-0203 requirement 6: the one list of (mode, transport) pairs this build plays. It is read by
    /// the account enrollment and the cast answer, so it may claim exactly what the playback paths
    /// open: LAN HTTP audio (the ADTS route), LAN HTTP video (the MPEG-TS shape of amendment item C,
    /// through the player window), RTSP video, and every mode over the RELAY listen of
    /// DEVICE-EXCHANGE section 7.6 (audio through the product's own pinned HTTP client, video
    /// through the engine - via the pin proxy when the descriptor pins the certificate). TUNNEL
    /// rides SP-0204's forwarder; P2P stays reserved and unimplemented.
    /// </summary>
    public static IReadOnlyList<ExchangePlayPair> Plays { get; } = Array.AsReadOnly(new[]
    {
        new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.AudioOnlyMode, "HTTP"),
        new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.VideoOnlyMode, "HTTP"),
        new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.VideoAudioMode, "HTTP"),
        new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.VideoOnlyMode, "RTSP"),
        new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.VideoAudioMode, "RTSP"),
        new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.AudioOnlyMode, "RELAY"),
        new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.VideoOnlyMode, "RELAY"),
        new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.VideoAudioMode, "RELAY"),
        new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.AudioOnlyMode, "TUNNEL"),
        new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.VideoOnlyMode, "TUNNEL"),
        new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.VideoAudioMode, "TUNNEL")
    });

    public static object Receiver => new
    {
        modes = Plays.Select(pair => pair.Mode).Distinct().ToArray(),
        transports = Plays.Select(pair => pair.Transport).Distinct().ToArray(),
        plays = Plays.Select(pair => new { mode = pair.Mode, transport = pair.Transport }).ToArray()
    };
}

public static class ExchangeProtocol
{
    public const int OpeningLimit = 16384;
    public const int AuthenticatedLimit = 262144;
    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "enroll", "enrolled", "hello", "welcome", "refused", "keepalive", "bye", "list", "directory",
        "subscribe", "changed", "publish", "published", "unpublish", "broadcast-start", "broadcast-started",
        "broadcast-update", "broadcast-end", "pairing-request", "pairing-code", "revoke", "connect", "open",
        "attach", "connected", "upload", "upload-accepted", "cast", "cast-offer", "cast-answer", "cast-result", "cast-stop"
    };
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public static bool IsKnownType(string? type) => type is not null && KnownTypes.Contains(type);

    public static string NewDeviceId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Fingerprint(ReadOnlySpan<byte> certificate) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(certificate)).TrimEnd('=');

    public static string RefusalKey(string? reason) => reason switch
    {
        "bad-credentials" => "ExchangeBadCredentials",
        "device-revoked" => "ExchangeDeviceRevoked",
        "version-unsupported" => "ExchangeVersionUnsupported",
        "tls-required" => "ExchangeTlsRequired",
        "id-collision" => "ExchangeIdCollision",
        "capacity" => "ExchangeCapacity",
        "port-unavailable" => "ExchangePortUnavailable",
        "unavailable" => "ExchangeUnavailable",
        "rate-limited" => "ExchangeRateLimited",
        _ => "ExchangeRefused"
    };

    public static async Task<JsonDocument> ReadAsync(Stream stream, bool authenticated, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        byte[]? body = null;
        try
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var limit = authenticated ? AuthenticatedLimit : OpeningLimit;
            if (length == 0 || length > limit)
            {
                throw new InvalidDataException("Invalid exchange frame length.");
            }

            body = new byte[length];
            await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
            using var input = new MemoryStream(body, writable: false);
            var document = JsonDocument.Parse(input);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new InvalidDataException("An exchange frame must be an object.");
            }

            return document;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        finally
        {
            if (body is not null)
            {
                CryptographicOperations.ZeroMemory(body);
            }
        }
    }

    public static async Task WriteAsync(Stream stream, object envelope, bool authenticated, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        try
        {
            if (body.Length > (authenticated ? AuthenticatedLimit : OpeningLimit))
            {
                throw new InvalidDataException("Exchange envelope exceeds the frame limit.");
            }

            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    public static async Task WriteEnrollmentAsync(Stream stream, object fields, string secretMember, char[] secret, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                using var document = JsonSerializer.SerializeToDocument(fields, JsonOptions);
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    property.WriteTo(writer);
                }

                writer.WriteString(secretMember, secret.AsSpan());
                writer.WriteEndObject();
            }

            if (buffer.Length > OpeningLimit)
            {
                throw new InvalidDataException("Exchange enrollment exceeds the frame limit.");
            }

            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)buffer.Length);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(secret);
            CryptographicOperations.ZeroMemory(buffer.GetBuffer());
        }
    }

    public static string? String(JsonElement envelope, string member) =>
        envelope.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
