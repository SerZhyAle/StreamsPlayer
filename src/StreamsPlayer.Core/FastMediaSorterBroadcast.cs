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
    long? TargetLatencyMs);

/// <summary>The supported, forward-compatible portion of a FastMediaSorter live-broadcast descriptor.</summary>
public sealed record FastMediaSorterBroadcast(
    string Url,
    string Title,
    string? SourceId,
    bool IsLive,
    long? TargetLatencyMs,
    IReadOnlyList<FastMediaSorterBroadcastEndpoint> Endpoints)
{
    /// <summary>
    /// Returns the first explicitly declared HTTP audio endpoint, or the legacy top-level address when
    /// no compatible endpoint is declared. A video endpoint is never an implicit fallback.
    /// </summary>
    public FastMediaSorterBroadcastEndpoint SelectAudioEndpoint() =>
        Endpoints.FirstOrDefault(IsHttpAudio) ??
        new FastMediaSorterBroadcastEndpoint(
            Url,
            InferTransport(Url),
            FastMediaSorterBroadcastDescriptor.AudioOnlyMode,
            null,
            null,
            null,
            null,
            IsLive,
            TargetLatencyMs);

    private static bool IsHttpAudio(FastMediaSorterBroadcastEndpoint endpoint) =>
        string.Equals(endpoint.Mode, FastMediaSorterBroadcastDescriptor.AudioOnlyMode, StringComparison.Ordinal) &&
        string.Equals(endpoint.Transport, "HTTP", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string? InferTransport(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Scheme.ToUpperInvariant()
            : null;
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

    private static FastMediaSorterBroadcastRead ReadText(string text)
    {
        if (text.StartsWith(CompressedPrefix, StringComparison.Ordinal))
        {
            return ReadCompressed(text[CompressedPrefix.Length..]);
        }

        if (TryReadIntentPayload(text, out var payload))
        {
            return payload is null
                ? new(FastMediaSorterBroadcastReadStatus.InvalidPayload)
                : ReadText(payload);
        }

        return text.StartsWith('{')
            ? ReadJson(text)
            : new(FastMediaSorterBroadcastReadStatus.NotBroadcast);
    }

    private static FastMediaSorterBroadcastRead ReadCompressed(string encoded)
    {
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
                if (decoded.Length + read > MaximumPayloadBytes)
                {
                    return new(FastMediaSorterBroadcastReadStatus.TooLarge);
                }

                decoded.Write(buffer, 0, read);
            }

            return Read(decoded.ToArray());
        }
        catch (FormatException)
        {
            return new(FastMediaSorterBroadcastReadStatus.InvalidPayload);
        }
        catch (InvalidDataException)
        {
            return new(FastMediaSorterBroadcastReadStatus.InvalidPayload);
        }
        catch (IOException)
        {
            return new(FastMediaSorterBroadcastReadStatus.InvalidPayload);
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

            if (!string.Equals(mode, AudioOnlyMode, StringComparison.Ordinal))
            {
                return new(FastMediaSorterBroadcastReadStatus.UnsupportedMode);
            }

            var title = TryGetString(root, "title") ?? string.Empty;
            var sourceId = TryGetString(root, "sourceId");
            var isLive = TryGetBoolean(root, "isLive") ?? true;
            var targetLatencyMs = ReadInt64(root, "targetLatencyMs");

            return new(
                FastMediaSorterBroadcastReadStatus.Ok,
                new FastMediaSorterBroadcast(url, title, sourceId, isLive, targetLatencyMs, ReadEndpoints(root)));
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
            if (endpoint.ValueKind != JsonValueKind.Object || !TryGetNonBlankString(endpoint, "url", out var url))
            {
                continue;
            }

            result.Add(new FastMediaSorterBroadcastEndpoint(
                url,
                TryGetString(endpoint, "transport"),
                TryGetString(endpoint, "mode"),
                TryGetString(endpoint, "videoCodec"),
                TryGetString(endpoint, "audioCodec"),
                ReadInt64(endpoint, "sampleRate"),
                ReadInt64(endpoint, "bitrate"),
                TryGetBoolean(endpoint, "isLive"),
                ReadInt64(endpoint, "targetLatencyMs")));
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
