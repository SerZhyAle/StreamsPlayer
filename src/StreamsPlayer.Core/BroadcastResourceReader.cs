using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// Reads a `.spres` broadcast resource document (`BROADCAST-RESOURCE` contract, schemaVersion 1),
/// in the shape of <see cref="ChannelShareText"/>: one status-bearing answer, never a throw. A
/// malformed document is a message to the user, not an exception in an <c>async void</c> handler.
/// </summary>
/// <remarks>
/// Platform-neutral on purpose: no WPF, no media dependency, and no network call anywhere in here -
/// the access-path race is <see cref="BroadcastUrl"/>, decided by the surface that plays.
/// Two transport variants are accepted, byte-identical in shape to the `.fmscfg` rule: a plain
/// UTF-8 JSON body (a `.spres` file, or a QR payload small enough to stay plain), and the
/// compressed QR form `SPRES1:` + base64(gzip(JSON)).
/// </remarks>
public static class BroadcastResourceReader
{
    /// <summary>The fixed prefix of the compressed QR payload (the sibling of `.fmscfg`'s `FMSCFG1:`).</summary>
    public const string CompressedPrefix = "SPRES1:";

    /// <summary>
    /// The byte ceiling of one document read. The import surfaces bound the file they hand over, but the
    /// reader faces hostile input too - a paste, a re-labelled file - and must answer it, not parse it.
    /// </summary>
    public const int MaximumDocumentBytes = 1024 * 1024;

    /// <summary>
    /// S14-4: the access paths kept from one document, in its order; later usable entries are dropped. They become the
    /// addresses a play races, and a producer lists a handful (lan, ipv6 and port-forward per adapter), not thousands.
    /// </summary>
    public const int MaximumAccessPaths = 16;

    /// <summary>S14-4: the longest <c>resourceName</c> kept; it becomes the channel title and is persisted with it.</summary>
    public const int MaximumResourceNameLength = 256;

    /// <summary>S14-4: the longest <c>accessNote</c> kept; a localized one-line hint, persisted with the channel.</summary>
    public const int MaximumAccessNoteLength = 1024;

    /// <summary>Reads one document from text (plain JSON or the compressed QR form).</summary>
    public static BroadcastResourceRead Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return BroadcastResourceRead.NotResourceDocument;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > MaximumDocumentBytes)
        {
            return BroadcastResourceRead.InvalidDocument;
        }
        if (trimmed.Length == 0)
        {
            return BroadcastResourceRead.NotResourceDocument;
        }

        if (trimmed.StartsWith(CompressedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // A compressed payload is unmistakably a broadcast resource document; a failure to decode it
            // is an unreadable document, not "not a document".
            var decompressed = TryDecompress(trimmed[CompressedPrefix.Length..]);
            if (decompressed is null)
            {
                return BroadcastResourceRead.InvalidDocument;
            }

            trimmed = decompressed;
        }

        if (trimmed.Length > MaximumDocumentBytes || !trimmed.StartsWith('{'))
        {
            return BroadcastResourceRead.NotResourceDocument;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed, new JsonDocumentOptions
            {
                MaxDepth = 16,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            return ReadDocument(document.RootElement);
        }
        catch (JsonException)
        {
            return BroadcastResourceRead.NotResourceDocument;
        }
        catch (InvalidOperationException)
        {
            // JsonElement rejects lone escaped surrogates when a string is materialized.
            return BroadcastResourceRead.InvalidDocument;
        }
    }

    private static BroadcastResourceRead ReadDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return BroadcastResourceRead.NotResourceDocument;
        }

        // schemaVersion first: reject anything greater than you support, by name.
        if (!root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
            schemaVersionElement.ValueKind != JsonValueKind.Number ||
            !schemaVersionElement.TryGetInt32(out var schemaVersion))
        {
            return BroadcastResourceRead.NotResourceDocument;
        }

        if (schemaVersion > BroadcastResource.SupportedSchemaVersion)
        {
            return BroadcastResourceRead.UnsupportedVersion;
        }

        if (schemaVersion < 1)
        {
            return BroadcastResourceRead.NotResourceDocument;
        }

        // The reserved discriminator: an unknown kind is not this document.
        if (!root.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String ||
            !string.Equals(kindElement.GetString(), "broadcast", StringComparison.Ordinal))
        {
            return BroadcastResourceRead.NotResourceDocument;
        }

        // protocol is ALWAYS "http" in v1; anything else is a document this build cannot honour.
        if (!root.TryGetProperty("protocol", out var protocolElement) ||
            protocolElement.ValueKind != JsonValueKind.String ||
            !string.Equals(protocolElement.GetString(), "http", StringComparison.Ordinal))
        {
            return BroadcastResourceRead.InvalidDocument;
        }

        if (!root.TryGetProperty("path", out var pathElement) ||
            pathElement.ValueKind != JsonValueKind.String ||
            pathElement.GetString() is not { Length: > 0 } path ||
            !path.StartsWith("/b/", StringComparison.Ordinal) ||
            path.Length <= 3 || path[3..].Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            return BroadcastResourceRead.InvalidDocument;
        }

        if (root.TryGetProperty("mediaKind", out var mediaKindElement))
        {
            if (mediaKindElement.ValueKind != JsonValueKind.String ||
                mediaKindElement.GetString() is not ("video" or "audio"))
            {
                return BroadcastResourceRead.InvalidDocument;
            }
        }

        var accessPaths = ReadAccessPaths(root);
        if (accessPaths.Count == 0)
        {
            // One path, many addresses: a document with no usable address cannot be raced, and racing is
            // the contract - so this is an unreadable document rather than a playable-but-dead one.
            return BroadcastResourceRead.InvalidDocument;
        }

        if (root.TryGetProperty("itemCount", out var itemCountElement))
        {
            if (itemCountElement.ValueKind != JsonValueKind.Number ||
                !itemCountElement.TryGetInt32(out var itemCount) ||
                itemCount < 1)
            {
                return BroadcastResourceRead.InvalidDocument;
            }
        }

        var playlist = false;
        if (root.TryGetProperty("playlist", out var playlistElement))
        {
            if (playlistElement.ValueKind != JsonValueKind.True && playlistElement.ValueKind != JsonValueKind.False)
            {
                return BroadcastResourceRead.InvalidDocument;
            }

            playlist = playlistElement.GetBoolean();
        }

        long durationSeconds = 0;
        if (root.TryGetProperty("durationSeconds", out var durationElement))
        {
            if (durationElement.ValueKind != JsonValueKind.Number ||
                !durationElement.TryGetInt64(out durationSeconds) ||
                durationSeconds < 0)
            {
                return BroadcastResourceRead.InvalidDocument;
            }
        }

        // expiresAt: absent or "" = until revoked; a value that does not parse is an unreadable document.
        DateTimeOffset? expiresAt = null;
        if (root.TryGetProperty("expiresAt", out var expiresElement))
        {
            if (expiresElement.ValueKind == JsonValueKind.String)
            {
                var raw = expiresElement.GetString();
                if (!string.IsNullOrEmpty(raw))
                {
                    if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    {
                        return BroadcastResourceRead.InvalidDocument;
                    }

                    expiresAt = parsed.ToUniversalTime();
                }
            }
            else if (expiresElement.ValueKind != JsonValueKind.Null)
            {
                return BroadcastResourceRead.InvalidDocument;
            }
        }

        DateTimeOffset? createdAt = null;
        if (root.TryGetProperty("createdAt", out var createdElement) && createdElement.ValueKind == JsonValueKind.String)
        {
            var raw = createdElement.GetString();
            if (!string.IsNullOrEmpty(raw) &&
                DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                createdAt = parsed.ToUniversalTime();
            }
        }

        var itemName = TextBoundary.Truncate(StringOrEmpty(root, "resourceName"), MaximumResourceNameLength);
        var note = TextBoundary.Truncate(StringOrEmpty(root, "accessNote"), MaximumAccessNoteLength);

        return new BroadcastResourceRead(BroadcastResourceStatus.Ok, new BroadcastResource(
            schemaVersion,
            itemName,
            root.TryGetProperty("mediaKind", out var kindValue) && kindValue.ValueKind == JsonValueKind.String
                ? kindValue.GetString() ?? "video"
                : "video",
            playlist,
            root.TryGetProperty("itemCount", out var countValue) && countValue.ValueKind == JsonValueKind.Number && countValue.TryGetInt32(out var count) ? count : 1,
            accessPaths,
            path,
            durationSeconds,
            expiresAt,
            note,
            createdAt));
    }

    /// <summary>
    /// The usable access paths, in the document's order. Unknown <c>kind</c> entries are skipped - that
    /// is the additive channel for v2 - and so is any entry without a host or with an impossible port. At most
    /// <see cref="MaximumAccessPaths"/> are kept (S14-4).
    /// </summary>
    private static IReadOnlyList<BroadcastAccessPath> ReadAccessPaths(JsonElement root)
    {
        if (!root.TryGetProperty("accessPaths", out var pathsElement) || pathsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var paths = new List<BroadcastAccessPath>();
        foreach (var entry in pathsElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!entry.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            // Known kinds only: lan, ipv6, portforward. An unknown kind is advice about a transport this
            // build does not speak, not an error.
            var kind = kindElement.GetString();
            if (kind is not ("lan" or "ipv6" or "portforward"))
            {
                continue;
            }

            if (!entry.TryGetProperty("host", out var hostElement) ||
                hostElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(hostElement.GetString()) ||
                !IsHost(hostElement.GetString()!))
            {
                continue;
            }

            if (!entry.TryGetProperty("port", out var portElement) ||
                portElement.ValueKind != JsonValueKind.Number ||
                !portElement.TryGetInt32(out var port) ||
                port is < 1 or > 65535)
            {
                continue;
            }

            paths.Add(new BroadcastAccessPath(kind, hostElement.GetString()!, port));
            if (paths.Count >= MaximumAccessPaths)
            {
                break;
            }
        }

        return paths;
    }

    private static bool IsHost(string host)
    {
        if (host.Contains('[') || host.Contains(']'))
        {
            return host.StartsWith('[') && host.EndsWith(']') &&
                Uri.CheckHostName(host[1..^1]) == UriHostNameType.IPv6;
        }

        return Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }

    private static string StringOrEmpty(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>base64(gzip(text)), or null when the payload is not one (the answer is then invalid, by the prefix).</summary>
    private static string? TryDecompress(string base64Payload)
    {
        try
        {
            var compressed = Convert.FromBase64String(base64Payload);
            using var compressedStream = new MemoryStream(compressed);
            using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (decompressed.Length + read > MaximumDocumentBytes)
                {
                    return null;
                }

                decompressed.Write(buffer, 0, read);
            }
            if (decompressed.Length == 0 || decompressed.Length > MaximumDocumentBytes)
            {
                return null;
            }

            return Encoding.UTF8.GetString(decompressed.ToArray());
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
}
