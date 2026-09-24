using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// Reads the current-title field from Icecast's small, server-owned status document.
/// </summary>
/// <remarks>
/// The document is not a public catalog contract: Icecast emits either one source object or an array,
/// and broadcasters can omit every optional field. This parser therefore treats a readable document with
/// no matching mount or title as an ordinary absence, while malformed JSON remains distinguishable to the
/// network caller. Returned text takes the same untrusted-broadcast path as ICY metadata.
/// </remarks>
public static class IcecastStatusParser
{
    /// <summary>
    /// Tries to read the status document for <paramref name="streamUri"/>.
    /// </summary>
    /// <returns><c>false</c> when the payload is not an Icecast status document; otherwise <c>true</c>,
    /// with a sanitized title or <c>null</c> when that mount currently announces none.</returns>
    public static bool TryExtractTitle(string payload, Uri streamUri, out string? title)
    {
        ArgumentNullException.ThrowIfNull(streamUri);
        title = null;

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("icestats", out var stats) ||
                stats.ValueKind != JsonValueKind.Object ||
                !stats.TryGetProperty("source", out var sources))
            {
                return false;
            }

            return sources.ValueKind switch
            {
                JsonValueKind.Object => TryExtractFromSource(sources, streamUri, out title),
                JsonValueKind.Array => TryExtractFromSources(sources, streamUri, out title),
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryExtractFromSources(JsonElement sources, Uri streamUri, out string? title)
    {
        foreach (var source in sources.EnumerateArray())
        {
            if (TryExtractFromSource(source, streamUri, out title))
            {
                return true;
            }
        }

        title = null;
        return false;
    }

    private static bool TryExtractFromSource(JsonElement source, Uri streamUri, out string? title)
    {
        title = null;
        if (source.ValueKind != JsonValueKind.Object ||
            !TryGetString(source, "listenurl", out var listenUrl) ||
            !Uri.TryCreate(listenUrl, UriKind.Absolute, out var listenUri) ||
            !SameMount(listenUri, streamUri))
        {
            return false;
        }

        foreach (var property in new[] { "title", "display-title", "yp_currently_playing" })
        {
            if (TryGetString(source, property, out var value))
            {
                title = BroadcastText.Sanitize(value, IcyMetadataParser.MaxTitleLength);
                break;
            }
        }

        return true;
    }

    private static bool TryGetString(JsonElement element, string property, out string? value)
    {
        value = null;
        return element.TryGetProperty(property, out var candidate) &&
               candidate.ValueKind == JsonValueKind.String &&
               (value = candidate.GetString()) is not null;
    }

    private static bool SameMount(Uri left, Uri right) =>
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port &&
        string.Equals(left.AbsolutePath.TrimEnd('/'), right.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
}
