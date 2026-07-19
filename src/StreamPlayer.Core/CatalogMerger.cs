namespace StreamPlayer.Core;

public static class CatalogMerger
{
    public static MergeResult Merge(
        IEnumerable<StreamChannel> existingChannels,
        IEnumerable<CatalogEntry> catalogEntries,
        DateTimeOffset now)
    {
        var existing = existingChannels.ToList();
        var byUrl = existing.ToDictionary(channel => channel.Url, StringComparer.Ordinal);
        var seenCatalogUrls = new HashSet<string>(StringComparer.Ordinal);
        var output = existing.ToDictionary(channel => channel.Id);
        var added = 0;
        var updated = 0;

        foreach (var entry in catalogEntries.GroupBy(item => item.Url, StringComparer.Ordinal).Select(group => group.First()))
        {
            seenCatalogUrls.Add(entry.Url);
            if (byUrl.TryGetValue(entry.Url, out var current))
            {
                if (current.SourceOrigin != SourceOrigin.Catalog)
                {
                    continue;
                }

                var replacement = current with
                {
                    Title = entry.Title,
                    MediaKind = entry.MediaKind,
                    Category = entry.Category,
                    Topic = entry.Topic,
                    Language = entry.Language,
                    Country = entry.Country,
                    Homepage = entry.Homepage,
                    FaviconIndex = entry.FaviconIndex
                };

                if (replacement != current)
                {
                    output[current.Id] = replacement;
                    updated++;
                }

                continue;
            }

            var channel = new StreamChannel
            {
                Id = Guid.NewGuid(),
                Url = entry.Url,
                Title = entry.Title,
                MediaKind = entry.MediaKind,
                SourceOrigin = SourceOrigin.Catalog,
                SortIndex = 0,
                AddedAt = now,
                Category = entry.Category,
                Topic = entry.Topic,
                Language = entry.Language,
                Country = entry.Country,
                Homepage = entry.Homepage,
                FaviconIndex = entry.FaviconIndex
            };
            output[channel.Id] = channel;
            byUrl[channel.Url] = channel;
            added++;
        }

        var removed = 0;
        foreach (var stale in existing.Where(channel =>
                     channel.SourceOrigin == SourceOrigin.Catalog && !seenCatalogUrls.Contains(channel.Url)))
        {
            output.Remove(stale.Id);
            removed++;
        }

        return new MergeResult(output.Values.ToList(), added, updated, removed);
    }
}
