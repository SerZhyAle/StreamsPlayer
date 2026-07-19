namespace StreamsPlayer.Core;

public static class M3uPlaylistParser
{
    public static IReadOnlyList<CatalogEntry> Parse(string text)
    {
        if (text.Contains("#EXT-X-", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var result = new List<CatalogEntry>();
        string? nextTitle = null;
        foreach (var originalLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = originalLine.Trim();
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',');
                nextTitle = comma >= 0 ? line[(comma + 1)..].Trim() : null;
                continue;
            }

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!StreamMediaKindClassifier.IsLaunchable(line))
            {
                nextTitle = null;
                continue;
            }

            var title = string.IsNullOrWhiteSpace(nextTitle)
                ? new Uri(line).Host
                : nextTitle;
            result.Add(new CatalogEntry(
                title,
                line,
                StreamMediaKindClassifier.Classify(line),
                null,
                null,
                null,
                null,
                null,
                null));
            nextTitle = null;
        }

        return result.GroupBy(entry => entry.Url, StringComparer.Ordinal).Select(group => group.First()).ToList();
    }
}
