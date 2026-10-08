using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>The result of reading an item offered to the FastMediaSorter broadcast import flow.</summary>
public enum FastMediaSorterBroadcastReadStatus
{
    NotBroadcast,
    Ok,
    TooLarge,
    InvalidEncoding,
    InvalidPayload,
    UnsupportedSchema,
    UnsupportedMode
}

/// <summary>One address advertised by a FastMediaSorter broadcast descriptor.</summary>
public sealed record FastMediaSorterBroadcastEndpoint(
    string Url,
    string? Transport,
    string? Mode,
    string? VideoCodec,
    string? AudioCodec,
    long? SampleRate,
    long? Bitrate,
    bool? IsLive,
    long? TargetLatencyMs,
    string? CertFingerprint = null,
    string? Inner = null);

/// <summary>The supported, forward-compatible portion of a FastMediaSorter live-broadcast descriptor.</summary>
public sealed record FastMediaSorterBroadcast(
    string Url,
    string Mode,
    string Title,
    string? SourceId,
    bool IsLive,
    long? TargetLatencyMs,
    IReadOnlyList<FastMediaSorterBroadcastEndpoint> Endpoints)
{
    /// <summary>Whether the descriptor describes one of the contract's camera/video kinds (§2.3, §2.4).</summary>
    public bool IsVideoMode => FastMediaSorterBroadcastDescriptor.IsVideoMode(Mode);

    /// <summary>
    /// Returns the endpoint this product plays: the first playable attempt of the producer's list
    /// (SP-0203) for this descriptor's mode, or the legacy top-level address when no compatible
    /// endpoint is declared. The transport is read from a declaration, never guessed from the
    /// mode - so an audio descriptor never falls into a video endpoint and the reverse.
    /// </summary>
    public FastMediaSorterBroadcastEndpoint SelectPlaybackEndpoint() =>
        IsVideoMode
            ? FastMediaSorterBroadcastAttempts.SelectVideo(Mode, Url, IsLive, TargetLatencyMs, Endpoints)
            : FastMediaSorterBroadcastAttempts.SelectAudio(Mode, Url, IsLive, TargetLatencyMs, Endpoints);

    /// <summary>
    /// Returns the first playable audio attempt in the listed order, or the legacy top-level address
    /// when no compatible endpoint is declared. A video endpoint is never an implicit fallback.
    /// </summary>
    public FastMediaSorterBroadcastEndpoint SelectAudioEndpoint() =>
        FastMediaSorterBroadcastAttempts.SelectAudio(Mode, Url, IsLive, TargetLatencyMs, Endpoints);

    /// <summary>
    /// SP-0203: the endpoints this build can play, in the producer's listed order - the order every
    /// playback leg tries them in, and the order a reconnect restarts at. A transport this build
    /// does not implement (`P2P` stays reserved) and an endpoint of another mode drop silently, as
    /// section 3.2 requires; a descriptor with no compatible list falls back to the legacy
    /// top-level address as the single attempt.
    /// </summary>
    public IReadOnlyList<FastMediaSorterBroadcastEndpoint> PlaybackAttemptEndpoints()
    {
        var attempts = FastMediaSorterBroadcastAttempts.PlaybackAttempts(Mode, Endpoints);
        return attempts.Count > 0
            ? attempts
            : [FastMediaSorterBroadcastAttempts.Legacy(Url, Mode, IsLive, TargetLatencyMs)];
    }
}

/// <summary>The outcome of parsing a FastMediaSorter broadcast hand-off without opening the network.</summary>
public sealed record FastMediaSorterBroadcastRead(
    FastMediaSorterBroadcastReadStatus Status,
    FastMediaSorterBroadcast? Broadcast = null)
{
    public bool IsAccepted => Status == FastMediaSorterBroadcastReadStatus.Ok;
}

/// <summary>
/// Contract parser for S3050 broadcasts. It accepts plain JSON, the barcode payload, and the current
/// Android intent link. This type deliberately owns no I/O: a hand-off becomes persisted data only after
/// its caller has shown the user what it will add or replace.
/// </summary>
public static class FastMediaSorterBroadcastDescriptor
{
    public const int MaximumPayloadBytes = 64 * 1024;
    public const int SupportedSchemaVersion = 1;
    public const string CompressedPrefix = "FMSBCAST1:";
    public const string AudioOnlyMode = "AUDIO_ONLY";
    public const string VideoAudioMode = "VIDEO_AUDIO";
    public const string VideoOnlyMode = "VIDEO_ONLY";

    /// <summary>
    /// SP-0158: the most wrappings one read follows - an intent-link unwrap and a gzip layer each cost
    /// one. The producer emits at most two (a link around a compressed token), so deeper nesting is not
    /// a shape anyone sends; it is the input being followed as deep as it goes, which the reader refuses
    /// rather than unwrapping without end.
    /// </summary>
    public const int MaximumWrappings = 8;

    /// <summary>
    /// The stream kinds this build reads: the audio kinds of §2.1/§2.2 and, since contract 0.13, the
    /// camera/video kinds of §2.3/§2.4. Anything else is refused as an unsupported mode.
    /// </summary>
    public static bool IsSupportedMode(string mode) =>
        mode is AudioOnlyMode or VideoAudioMode or VideoOnlyMode;

    /// <summary>SP-0203: whether the mode is one of the contract's camera/video kinds.</summary>
    public static bool IsVideoMode(string mode) =>
        mode is VideoAudioMode or VideoOnlyMode;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static FastMediaSorterBroadcastRead Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumPayloadBytes)
        {
            return new(FastMediaSorterBroadcastReadStatus.TooLarge);
        }

        try
        {
            return ReadText(StrictUtf8.GetString(bytes).Trim());
        }
        catch (DecoderFallbackException)
        {
            return new(FastMediaSorterBroadcastReadStatus.InvalidEncoding);
        }
    }

    public static FastMediaSorterBroadcastRead Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new(FastMediaSorterBroadcastReadStatus.NotBroadcast);
        }

        try
        {
            if (StrictUtf8.GetByteCount(text) > MaximumPayloadBytes)
            {
                return new(FastMediaSorterBroadcastReadStatus.TooLarge);
            }
        }
        catch (EncoderFallbackException)
        {
            return new(FastMediaSorterBroadcastReadStatus.InvalidEncoding);
        }

        return ReadText(text.Trim());
    }

    /// <summary>
    /// SP-0158: unwraps in a loop, never by calling itself - a nested link or compressed layer used to
    /// recurse one frame per level, and a payload within the size cap could nest thousands of levels.
    /// </summary>
    private static FastMediaSorterBroadcastRead ReadText(string text)
    {
        var wrappingsLeft = MaximumWrappings;
        // The whole read's inflation budget, not one layer's: the size ceiling applies once, so a set of
        // compressed layers that each fit is refused when their inflated total passes it.
        var inflatedBytes = 0L;
        while (true)
        {
            // S9-2: a file saved by a Windows editor opens with a UTF-8 BOM, which the strict decode keeps
            // as U+FEFF and string.Trim does not treat as white space - so the layer no longer started
            // with '{' or a known prefix and a valid descriptor read as "not a broadcast". Every layer
            // (outer, unwrapped link payload, inflated text) is cleaned the same way.
            text = text.TrimStart('﻿').Trim();
            if (text.StartsWith(CompressedPrefix, StringComparison.Ordinal))
            {
                if (wrappingsLeft == 0)
                {
                    return new(FastMediaSorterBroadcastReadStatus.InvalidPayload);
                }

                wrappingsLeft--;
                var inflated = Decompress(text[CompressedPrefix.Length..], ref inflatedBytes, out var failure);
                if (inflated is null)
                {
                    return new(failure);
                }

                text = inflated;
                continue;
            }

            if (TryReadIntentPayload(text, out var payload))
            {
                if (payload is null || wrappingsLeft == 0)
                {
                    return new(FastMediaSorterBroadcastReadStatus.InvalidPayload);
                }

                wrappingsLeft--;
                text = payload;
                continue;
            }

            return text.StartsWith('{')
                ? ReadJson(text)
                : new(FastMediaSorterBroadcastReadStatus.NotBroadcast);
        }
    }

    /// <summary>Inflates one <c>FMSBCAST1:</c> layer against the read's shared budget.</summary>
    /// <returns>The decoded, trimmed layer text, or <c>null</c> with the refusal status in <paramref name="failure"/>.</returns>
    private static string? Decompress(string encoded, ref long inflatedBytes, out FastMediaSorterBroadcastReadStatus failure)
    {
        failure = FastMediaSorterBroadcastReadStatus.InvalidPayload;
        try
        {
            var compressed = Convert.FromBase64String(encoded);
            using var source = new MemoryStream(compressed, writable: false);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                inflatedBytes += read;
                if (inflatedBytes > MaximumPayloadBytes)
                {
                    failure = FastMediaSorterBroadcastReadStatus.TooLarge;
                    return null;
                }

                decoded.Write(buffer, 0, read);
            }

            // The strict decode is the byte entry's rule: a layer that inflates to non-UTF-8 is an
            // encoding failure, not a malformed payload.
            failure = FastMediaSorterBroadcastReadStatus.InvalidEncoding;
            return StrictUtf8.GetString(decoded.GetBuffer(), 0, (int)decoded.Length).Trim();
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static FastMediaSorterBroadcastRead ReadJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new(FastMediaSorterBroadcastReadStatus.InvalidPayload);
            }

            var root = document.RootElement;
            if (ReadInt64(root, "schemaVersion") is not { } schemaVersion)
            {
                return new(FastMediaSorterBroadcastReadStatus.InvalidPayload);
            }

            if (schemaVersion > SupportedSchemaVersion)
            {
                return new(FastMediaSorterBroadcastReadStatus.UnsupportedSchema);
            }

            if (schemaVersion != SupportedSchemaVersion ||
                !TryGetNonBlankString(root, "url", out var url) ||
                !TryGetNonBlankString(root, "mode", out var mode))
            {
                return new(FastMediaSorterBroadcastReadStatus.InvalidPayload);
            }

            // SP-0159: §2.3 and §2.4 are contract kinds since 0.13, read in the §2.5 shape. A mode
            // outside the contract stays refused - never guessed into a transport.
            if (!IsSupportedMode(mode))
            {
                return new(FastMediaSorterBroadcastReadStatus.UnsupportedMode);
            }

            // A9-1: the address is persisted and later handed to a playback engine, so it must be one the
            // product launches anywhere else (http, https, rtsp with a host) - never a file path or share.
            if (!LaunchableAddress.IsLaunchable(url))
            {
                return new(FastMediaSorterBroadcastReadStatus.InvalidPayload);
            }

            var title = TryGetString(root, "title") ?? string.Empty;
            var sourceId = TryGetString(root, "sourceId");
            var isLive = TryGetBoolean(root, "isLive") ?? true;
            var targetLatencyMs = ReadInt64(root, "targetLatencyMs");

            return new(
                FastMediaSorterBroadcastReadStatus.Ok,
                new FastMediaSorterBroadcast(url, mode, title, sourceId, isLive, targetLatencyMs, ReadEndpoints(root)));
        }
        catch (JsonException)
        {
            return new(FastMediaSorterBroadcastReadStatus.InvalidPayload);
        }
    }

    private static IReadOnlyList<FastMediaSorterBroadcastEndpoint> ReadEndpoints(JsonElement root)
    {
        if (!root.TryGetProperty("endpoints", out var endpoints) || endpoints.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<FastMediaSorterBroadcastEndpoint>();
        foreach (var endpoint in endpoints.EnumerateArray())
        {
            if (endpoint.ValueKind != JsonValueKind.Object ||
                !TryGetNonBlankString(endpoint, "url", out var url))
            {
                continue;
            }

            var transport = TryGetString(endpoint, "transport");
            var inner = TryGetString(endpoint, "inner");
            var isTunnel = string.Equals(transport, "TUNNEL", StringComparison.OrdinalIgnoreCase)
                && ExchangeTunnelUrl.TryParse(url, out _)
                && !string.IsNullOrWhiteSpace(inner)
                && LaunchableAddress.IsLaunchable(inner);

            // A9-1: an endpoint nobody can launch is dropped rather than stored beside the usable ones.
            if (!isTunnel && !LaunchableAddress.IsLaunchable(url))
            {
                continue;
            }

            result.Add(new FastMediaSorterBroadcastEndpoint(
                url,
                transport,
                TryGetString(endpoint, "mode"),
                TryGetString(endpoint, "videoCodec"),
                TryGetString(endpoint, "audioCodec"),
                ReadInt64(endpoint, "sampleRate"),
                ReadInt64(endpoint, "bitrate"),
                TryGetBoolean(endpoint, "isLive"),
                ReadInt64(endpoint, "targetLatencyMs"),
                TryGetString(endpoint, "certFingerprint"),
                inner));
        }

        return result;
    }

    private static bool TryReadIntentPayload(string text, out string? payload)
    {
        payload = null;
        if (text.StartsWith("fmsbcast://", StringComparison.OrdinalIgnoreCase))
        {
            return TryExtractFmsBroadcastQuery(text, out payload);
        }

        if (!text.StartsWith("intent://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var intentStart = text.IndexOf("#Intent;", StringComparison.OrdinalIgnoreCase);
        if (intentStart < 0 ||
            !text[intentStart..].Contains(";scheme=fmsbcast;", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryExtractFmsBroadcastQuery(text[..intentStart], out payload);
    }

    private static bool TryExtractFmsBroadcastQuery(string text, out string? payload)
    {
        payload = null;
        var queryStart = text.IndexOf('?');
        if (queryStart < 0)
        {
            return true;
        }

        foreach (var pair in text[(queryStart + 1)..].Split('&'))
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            if (!string.Equals(name, "payload", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                payload = Uri.UnescapeDataString(separator < 0 ? string.Empty : pair[(separator + 1)..]);
            }
            catch (UriFormatException)
            {
                payload = null;
            }

            return true;
        }

        return true;
    }

    private static string? TryGetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;

    private static bool TryGetNonBlankString(JsonElement element, string name, out string value)
    {
        value = TryGetString(element, name) ?? string.Empty;
        return value.Length > 0;
    }

    /// <summary>An absent integer field is null; a present one that is not a whole JSON number is a broken payload.</summary>
    /// <remarks>
    /// SP-0119: <see cref="JsonElement.TryGetInt64"/> throws <see cref="InvalidOperationException"/> - not a
    /// <see cref="JsonException"/> - on a string, <c>null</c> or boolean value, and that escaped the reader's
    /// catch into the paste and drop handlers, which ended the process on any clipboard JSON that happened to
    /// carry the right field names. Throwing <see cref="JsonException"/> here routes a wrongly typed field to
    /// the same invalid-payload answer as malformed JSON, the way the artwork-manifest reader treats one.
    /// </remarks>
    private static long? ReadInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value)
            ? value
            : throw new JsonException($"'{name}' is not an integer.");
    }

    private static bool? TryGetBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
}
