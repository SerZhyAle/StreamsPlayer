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

    /// <summary>
    /// Item AM: the most pairs an `enroll` may declare. A server closes an enrollment that exceeds it without
    /// an answer, so the bound is kept here and not rediscovered from a dropped connection.
    /// </summary>
    public const int MaximumDeclaredPairs = 8;

    public static object Receiver => Declare(Plays);

    /// <summary>
    /// The `receiver` member of an `enroll`. Item AM lets `plays` stay out when the product of `modes` and
    /// `transports` exceeds <see cref="MaximumDeclaredPairs"/>, and item E reads the two lists as that same
    /// product, so omitting it is the declaration the contract asks for and not a weaker one; the exact pairs
    /// stay in <see cref="Plays"/> for the cast answers.
    /// </summary>
    public static object Declare(IReadOnlyList<ExchangePlayPair> plays)
    {
        var modes = plays.Select(pair => pair.Mode).Distinct().ToArray();
        var transports = plays.Select(pair => pair.Transport).Distinct().ToArray();
        if (modes.Length * transports.Length > MaximumDeclaredPairs)
        {
            return new { modes, transports };
        }

        return new
        {
            modes,
            transports,
            plays = plays.Select(pair => new { mode = pair.Mode, transport = pair.Transport }).ToArray()
        };
    }
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
        "attach", "connected", "upload", "upload-accepted", "cast", "cast-offer", "cast-answer", "cast-result", "cast-stop",
        "password-change", "password-changed"
    };
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public static bool IsKnownType(string? type) => type is not null && KnownTypes.Contains(type);

    public static string NewDeviceId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Fingerprint(ReadOnlySpan<byte> certificate) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(certificate)).TrimEnd('=');

    /// <summary>
    /// The interface key of a <c>refused.reason</c>: the text of its row in the family's table of messages
    /// (<c>DEVICE-EXCHANGE</c> item CZ). An unknown reason is the generic refusal. <paramref name="oneTimeCode"/>
    /// selects the table's separate wording for a wrong one-time code (the pairing code).
    /// </summary>
    public static string RefusalKey(string? reason, bool oneTimeCode = false) =>
        ExchangeFailureRows.OfWire(reason, oneTimeCode).MessageKey;

    /// <summary>
    /// Item AY: the leaf's public key, kept next to the leaf fingerprint so a renewal with the same key is told
    /// from a different server. <c>SHA256:</c> over the DER SubjectPublicKeyInfo, in the same written form as
    /// <see cref="Fingerprint"/>; null when the bytes are not a certificate. It is kept on the device only and
    /// never written into a string, a descriptor or a record.
    /// </summary>
    public static string? PublicKeyFingerprint(ReadOnlySpan<byte> certificate)
    {
        try
        {
            using var parsed = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                .LoadCertificate(certificate);
            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(parsed.PublicKey.ExportSubjectPublicKeyInfo()))
                .TrimEnd('=');
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

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

    /// <summary>
    /// A string member of a frame, or <see langword="null"/> when it is absent, not a string, or not valid
    /// text. A lone-surrogate escape (<c>"\ud800"</c>) parses as a string element but makes
    /// <see cref="JsonElement.GetString"/> throw <see cref="InvalidOperationException"/>, which a peer must
    /// never be able to use to end the receive loop (SP-0180).
    /// </summary>
    public static string? String(JsonElement envelope, string member)
    {
        if (!envelope.TryGetProperty(member, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
