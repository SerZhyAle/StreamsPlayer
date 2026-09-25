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
        var byNormalizedUrl = new Dictionary<string, StreamChannel>(StringComparer.Ordinal);
        foreach (var channel in existing)
        {
            var normalized = CatalogUrlIdentity.Normalize(channel.Url);
            byNormalizedUrl.TryAdd(normalized, channel);
        }

        var seenCatalogUrls = new HashSet<string>(StringComparer.Ordinal);
        var reindexed = new HashSet<Guid>();
        var output = existing.ToDictionary(channel => channel.Id);
        var added = 0;
        var updated = 0;

        foreach (var entry in catalogEntries.GroupBy(item => CatalogUrlIdentity.Normalize(item.Url), StringComparer.Ordinal).Select(group => group.First()))
        {
            var normalizedUrl = CatalogUrlIdentity.Normalize(entry.Url);
            seenCatalogUrls.Add(normalizedUrl);

            if (byNormalizedUrl.TryGetValue(normalizedUrl, out var current))
            {
                // SP-0098 Decision 5: Explicitly separate update rights by provenance:
                // - Published catalog (Catalog) can update Catalog and LocalCatalog rows (published metadata wins, Decision 2 & 28).
                // - Local bank import (LocalCatalog) can update LocalCatalog rows only; it never overwrites Catalog or user rows.
                // - User-created rows (Manual / Imported) are never updated by either catalog path.
                if (!CanUpdate(current.SourceOrigin, options.TargetOrigin))
                {
                    continue;
                }

                reindexed.Add(current.Id);
                var replacement = current with
                {
                    Title = entry.Title,
                    MediaKind = entry.MediaKind,
                    SourceOrigin = options.TargetOrigin == SourceOrigin.Catalog ? SourceOrigin.Catalog : current.SourceOrigin,
                    Category = entry.Category,
                    Topic = entry.Topic,
                    Language = entry.Language,
                    Country = entry.Country,
                    Homepage = entry.Homepage,
                    FaviconIndex = entry.FaviconIndex,
                    // SP-0052 & SP-0098: the index and the atlas it indexes move together or not at all.
                    FaviconSource = options.FaviconSource,
                    Protocol = entry.Protocol,
                    Format = entry.Format,
                    Bitrate = entry.Bitrate,
                    IsLive = entry.IsLive,
                    Access = entry.Access,
                    // SP-0089: the bank lists this URL again, so the row is on offer again - unless this
                    // bank cannot speak for the present (SP-0126: the bundled snapshot).
                    RetiredAt = options.RevivesRetired ? null : current.RetiredAt
                };

                if (replacement != current)
                {
                    output[current.Id] = replacement;
                    byNormalizedUrl[normalizedUrl] = replacement;
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
                SourceOrigin = options.TargetOrigin,
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
            byNormalizedUrl[normalizedUrl] = channel;
            reindexed.Add(channel.Id);
            added++;
        }

        var removed = 0;
        // SP-0052 & SP-0098: Pruning applies only when requested (RemoveMissing: true) and only to
        // published Catalog rows. LocalCatalog, Manual, and Imported rows are NEVER pruned by catalog refresh.
        if (options.RemoveMissing && options.TargetOrigin == SourceOrigin.Catalog)
        {
            foreach (var stale in existing.Where(channel =>
                         channel.SourceOrigin == SourceOrigin.Catalog && !seenCatalogUrls.Contains(CatalogUrlIdentity.Normalize(channel.Url))))
            {
                // SP-0089, STREAM-BANK item D: absence is authority to stop offering a channel, never
                // authority to delete what the user made about it.
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
        if (options.ReplacesAtlas)
        {
            // SP-0125, STREAM-BANK rule 6 / item A: an index is an offset into the sheet of its own bank. This
            // slot's sheet is being replaced, so an index these entries did not just write points into a sheet
            // about to be deleted - and resolved against the new one it would show another channel's icon.
            // The monogram is the honest answer until a bank lists the row again.
            for (var i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                if (channel.FaviconIndex is not null &&
                    channel.FaviconSource == options.FaviconSource &&
                    !reindexed.Contains(channel.Id))
                {
                    channels[i] = channel with { FaviconIndex = null };
                }
            }
        }

        return new MergeResult(
            channels,
            added,
            updated,
            removed,
            channels.Count(channel => channel.RetiredAt is not null));
    }

    private static bool CanUpdate(SourceOrigin currentOrigin, SourceOrigin incomingOrigin) =>
        incomingOrigin switch
        {
            SourceOrigin.Catalog => currentOrigin is SourceOrigin.Catalog or SourceOrigin.LocalCatalog,
            SourceOrigin.LocalCatalog => currentOrigin == SourceOrigin.LocalCatalog,
            _ => false
        };
}
