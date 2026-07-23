namespace StreamsPlayer.Core;

public static class StreamCatalogCsvParser
{
    public static IReadOnlyList<CatalogEntry> Parse(string csv)
    {
        var rows = Rfc4180Csv.Parse(csv);
        if (rows.Count == 0)
        {
            return [];
        }

        var header = rows[0]
            .Select((name, index) => (Name: name.Trim().TrimStart('\uFEFF'), Index: index))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);

        var result = new List<CatalogEntry>(Math.Max(0, rows.Count - 1));
        foreach (var row in rows.Skip(1))
        {
            var title = Cell("name").Trim();
            var url = Cell("url").Trim();
            if (title.Length == 0 || url.Length == 0)
            {
                continue;
            }

            int? faviconIndex = null;
            if (int.TryParse(Cell("favicon_index").Trim(), out var parsedIndex) && parsedIndex >= 0)
            {
                faviconIndex = parsedIndex;
            }

            result.Add(new CatalogEntry(
                title,
                url,
                StreamMediaKindClassifier.FromCatalogValue(Cell("media_kind"), url),
                Optional("category"),
                Optional("topic"),
                Optional("language"),
                Optional("country"),
                Optional("homepage"),
                faviconIndex,
                Optional("protocol"),
                Optional("format"),
                Optional("bitrate"),
                ParseIsLive(Cell("is_live"))));

            string Cell(string name) =>
                header.TryGetValue(name, out var index) && index < row.Count ? row[index] : string.Empty;

            string? Optional(string name)
            {
                var value = Cell(name).Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return result;
    }

    // Tolerant parse of the optional, untrusted is_live claim. Unknown/blank/absent stays null.
    private static bool? ParseIsLive(string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "live" => true,
        "false" or "0" or "no" or "vod" => false,
        _ => null
    };
}
