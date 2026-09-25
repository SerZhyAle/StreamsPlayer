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
                ParseIsLive(Cell("is_live")),
                ParseAccess(Cell("access"))));

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

    // SP-0108, STREAM-BANK `03_catalog_format.md` §2.3: a trimmed, case-insensitive "true" is the only
    // true; every other non-empty value - "false", "1", "yes", anything - is false. A blank cell (and an
    // absent column) stays null, "the producer did not say", where the contract says false: that half is
    // this product's proposal to the contract owner, carried as a dated exception in the registry until
    // it is ruled on. SP-0033 read "1" and "yes" as true - the inverse of the contract.
    private static bool? ParseIsLive(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    // SP-0088, STREAM-BANK item E: `access` is an opaque token, not a closed set. Blank - and an
    // absent column, which reads as blank - means open; any non-empty value means a restriction this
    // consumer does not model. Switching on the single known token `geo` and folding everything else
    // into "open" was the inversion of that rule: a token the producer adds later would be read as the
    // absence of a restriction, which is the one answer that cannot be right. The producer currently
    // drops region-locked rows instead of tagging them, so this branch is unreachable against today's
    // bank (0 of 18 908 rows) - it exists so it starts working on its own if a producer returns.
    private static ChannelAccess ParseAccess(string value) =>
        value.Trim().Length == 0 ? ChannelAccess.Open : ChannelAccess.GeoRestricted;
}
