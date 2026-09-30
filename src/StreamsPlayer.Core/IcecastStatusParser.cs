using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// Reads the current-title field from Icecast's small, server-owned status document.
/// </summary>
/// <remarks>
/// The document is not a public catalog contract: Icecast emits either one source object or an array,
/// and broadcasters can omit every optional field. SP-0172: a readable document that lists other mounts
/// reports <see cref="IcecastStatusParse.NoMatchingMount"/> - an ordinary absence - while a payload that
/// is not a status document at all reports <see cref="IcecastStatusParse.NotStatusDocument"/>, so the
/// network caller can log the two differently. Returned text takes the same untrusted-broadcast path as
/// ICY metadata.
/// </remarks>
public static class IcecastStatusParser
{
    /// <summary>
    /// Reads the status document for <paramref name="streamUri"/>.
    /// </summary>
    /// <returns>What the payload turned out to be; <paramref name="title"/> carries a sanitized title or
    /// <c>null</c> only for <see cref="IcecastStatusParse.MatchedMount"/>, the latter when that mount
    /// currently announces none.</returns>
    public static IcecastStatusParse ExtractTitle(string payload, Uri streamUri, out string? title)
    {
        ArgumentNullException.ThrowIfNull(streamUri);
        title = null;

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("icestats", out var stats) ||
                stats.ValueKind != JsonValueKind.Object)
            {
                return IcecastStatusParse.NotStatusDocument;
            }

            // A status document without a source list, or with an empty one, is a server that currently
            // publishes no mounts - a readable absence, not a broken answer.
            if (!stats.TryGetProperty("source", out var sources))
            {
                return IcecastStatusParse.NoMatchingMount;
            }

            IReadOnlyList<JsonElement> candidates = sources.ValueKind switch
            {
                JsonValueKind.Object => [sources],
                JsonValueKind.Array => [.. sources.EnumerateArray()],
                _ => []
            };

            return TryExtractFromSources(candidates, streamUri, out title)
                ? IcecastStatusParse.MatchedMount
                : IcecastStatusParse.NoMatchingMount;
        }
        catch (JsonException)
        {
            return IcecastStatusParse.NotStatusDocument;
        }
    }

    /// <summary>
    /// An exact host, port and path match wins. Failing that, SP-0131: the one source whose mount path is
    /// the playing path. A server left with its stock <c>hostname</c> reports <c>localhost</c>, and one
    /// behind a proxy reports its own port, so an exact-only rule sent every such station to a second
    /// audio download just to read titles. Two sources on the same path under different hosts stay
    /// unmatched: guessing between them could show another station's track.
    /// </summary>
    private static bool TryExtractFromSources(IReadOnlyList<JsonElement> sources, Uri streamUri, out string? title)
    {
        JsonElement? samePath = null;
        var samePathCount = 0;
        foreach (var source in sources)
        {
            if (!TryGetListenUri(source, out var listenUri) || !SamePath(listenUri, streamUri))
            {
                continue;
            }

            if (SameAuthority(listenUri, streamUri))
            {
                title = ExtractTitle(source);
                return true;
            }

            samePath = source;
            samePathCount++;
        }

        if (samePathCount == 1)
        {
            title = ExtractTitle(samePath!.Value);
            return true;
        }

        title = null;
        return false;
    }

    private static bool TryGetListenUri(JsonElement source, out Uri listenUri)
    {
        listenUri = null!;
        return source.ValueKind == JsonValueKind.Object &&
               TryGetString(source, "listenurl", out var listenUrl) &&
               Uri.TryCreate(listenUrl, UriKind.Absolute, out listenUri!);
    }

    private static string? ExtractTitle(JsonElement source)
    {
        foreach (var property in new[] { "title", "display-title", "yp_currently_playing" })
        {
            if (TryGetString(source, property, out var value))
            {
                return BroadcastText.Sanitize(value, IcyMetadataParser.MaxTitleLength);
            }
        }

        return null;
    }

    private static bool TryGetString(JsonElement element, string property, out string? value)
    {
        value = null;
        return element.TryGetProperty(property, out var candidate) &&
               candidate.ValueKind == JsonValueKind.String &&
               (value = candidate.GetString()) is not null;
    }

    private static bool SameAuthority(Uri left, Uri right) =>
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool SamePath(Uri left, Uri right) =>
        string.Equals(left.AbsolutePath.TrimEnd('/'), right.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
}
