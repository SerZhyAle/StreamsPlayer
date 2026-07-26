using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0031 reader for the channel-preview sidecar: a flat JSON object mapping a stream URL to its
/// zero-based tile ordinal. URL is the stable per-channel key, the same one the catalog merge and the
/// preview store use.
/// </summary>
public static class ChannelPreviewCoords
{
    /// <summary>
    /// Parses the sidecar. A blank document or a non-object root yields an empty map - "no atlas
    /// installed" is an expected state, not a fault. Individual entries whose value is not an integer
    /// are skipped rather than throwing, so one malformed row cannot poison the whole map.
    /// Malformed JSON throws <see cref="JsonException"/> for the caller to treat as "not installed".
    /// </summary>
    public static IReadOnlyDictionary<string, int> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (TryReadIndex(property.Value, out var index))
            {
                map[property.Name] = index;
            }
        }

        return map;
    }

    private static bool TryReadIndex(JsonElement value, out int index)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt32(out index);
            case JsonValueKind.String:
                return int.TryParse(value.GetString(), out index);
            default:
                index = 0;
                return false;
        }
    }
}
