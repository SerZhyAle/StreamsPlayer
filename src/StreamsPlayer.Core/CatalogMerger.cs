namespace StreamsPlayer.Core;

public static class CatalogMerger
{
    /// <param name="channelsWithUserData">
    /// Ids that a missing URL may not take down with it, from <see cref="UserAuthoredChannels.Identify"/>
    /// (SP-0089). Null means "nothing outside the rows is protected" - the rows' own
    /// <see cref="StreamChannel.Pinned"/> flag is still honoured, so a caller that has no collections or
    /// history to consult cannot accidentally opt out of the whole rule by omitting the argument.
    /// </param>
    public static MergeResult Merge(
        IEnumerable<StreamChannel> existingChannels,
        IEnumerable<CatalogEntry> catalogEntries,
        DateTimeOffset now,
        CatalogMergeOptions? options = null,
        IReadOnlySet<Guid>? channelsWithUserData = null)
    {
        options ??= CatalogMergeOptions.CatalogRefresh;
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
                    FaviconIndex = entry.FaviconIndex,
                    // SP-0052: the index and the atlas it indexes move together or not at all. A row a
                    // download brought in and a snapshot then updated points at the snapshot's atlas.
                    FaviconSource = options.FaviconSource,
                    Protocol = entry.Protocol,
                    Format = entry.Format,
                    Bitrate = entry.Bitrate,
                    IsLive = entry.IsLive,
                    Access = entry.Access,
                    // SP-0089: the bank lists this URL again, so the row is on offer again. Because the
                    // row was kept rather than deleted, its id never changed and the pin, the collection
                    // membership and the history entry that reference it are simply correct again - there
                    // is nothing to reattach, which is the whole reason retiring beats tombstoning
                    // somewhere else. Unconditional: a row that was never retired writes null over null.
                    RetiredAt = null
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
                FaviconIndex = entry.FaviconIndex,
                FaviconSource = options.FaviconSource,
                Protocol = entry.Protocol,
                Format = entry.Format,
                Bitrate = entry.Bitrate,
                IsLive = entry.IsLive,
                Access = entry.Access
            };
            output[channel.Id] = channel;
            byUrl[channel.Url] = channel;
            added++;
        }

        var removed = 0;
        // SP-0052: the one branch the bundled snapshot takes differently. Pruning means "the bank no
        // longer publishes this channel", which only the bank itself can assert; a snapshot is a copy of
        // an older bank, so its silence about a URL says nothing about whether the channel still exists.
        if (options.RemoveMissing)
        {
            foreach (var stale in existing.Where(channel =>
                         channel.SourceOrigin == SourceOrigin.Catalog && !seenCatalogUrls.Contains(channel.Url)))
            {
                // SP-0089, source contract item D: absence is authority to stop offering a channel, never
                // authority to delete what the user made about it. A row nobody touched still goes - the
                // catalog is not an archive - but one carrying a pin, a collection membership or a history
                // entry is retired instead, keeping its id so those references stay valid. Deleting it
                // would be unrecoverable even by the producer: a later bank republishing the identical URL
                // mints a new row, so the pin does not come back when the channel does.
                if (stale.Pinned || channelsWithUserData?.Contains(stale.Id) == true)
                {
                    if (stale.RetiredAt is null)
                    {
                        output[stale.Id] = stale with { RetiredAt = now };
                    }

                    continue;
                }

                output.Remove(stale.Id);
                removed++;
            }
        }

        var channels = output.Values.ToList();
        return new MergeResult(
            channels,
            added,
            updated,
            removed,
            channels.Count(channel => channel.RetiredAt is not null));
    }
}
