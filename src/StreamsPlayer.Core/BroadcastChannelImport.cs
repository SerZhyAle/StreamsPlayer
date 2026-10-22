namespace StreamsPlayer.Core;

/// <summary>How a broadcast resource was applied to the persisted channel list.</summary>
public sealed record BroadcastChannelApplyResult(
    IReadOnlyList<StreamChannel> Channels,
    StreamChannel Channel,
    bool Added);

/// <summary>
/// Applies a validated broadcast resource to the channel list, without I/O. Mirrors the M3U import's
/// additivity and the exchange descriptor's replace-by-identity in one place: an import adds ONE
/// channel with <see cref="SourceOrigin.Imported"/>, so <c>CatalogMerger</c> never updates or prunes it
/// on a catalog refresh - and a re-imported refreshed document for the same broadcast UPDATES that row
/// instead of adding a second one, keyed by the resource's <c>path</c>.
/// </summary>
public static class BroadcastChannelImport
{
    /// <summary>
    /// Builds the channel row of one resource: the first composed address is the row's URL (the race
    /// re-resolves it at play time), the rest are its <see cref="StreamChannel.AlternateUrls"/>. Without
    /// the alternates a channel is pinned to one subnet and dies when the PC's LAN address changes.
    /// </summary>
    public static StreamChannel BuildChannel(BroadcastResource resource, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var urls = BroadcastUrl.ComposeAll(resource);
        return new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = urls[0],
            Title = string.IsNullOrWhiteSpace(resource.ResourceName) ? "Broadcast" : resource.ResourceName.Trim(),
            MediaKind = resource.MediaKind == "audio" ? MediaKind.Audio : MediaKind.Video,
            SourceOrigin = SourceOrigin.Imported,
            SortIndex = 0,
            AddedAt = now,
            AlternateUrls = urls.Count > 1 ? urls.Skip(1).ToArray() : null,
            ResourceKey = resource.Path,
            ResourceExpiresAt = resource.ExpiresAt,
            IsPlaylistResource = resource.Playlist,
            ResourceAccessNote = string.IsNullOrWhiteSpace(resource.AccessNote) ? null : resource.AccessNote.Trim()
        };
    }

    /// <summary>
    /// Applies <paramref name="resource"/> to <paramref name="existing"/>: a row whose
    /// <see cref="StreamChannel.ResourceKey"/> matches the resource's path is REPLACED (same id, so
    /// pins, collections and history reattach by themselves); anything else ADDS one row at the end.
    /// The sort index of an added row is the caller's to set - this returns the row with 0.
    /// </summary>
    public static BroadcastChannelApplyResult Apply(
        IEnumerable<StreamChannel> existing,
        BroadcastResource resource,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(resource);

        var channels = existing.ToList();
        var refreshed = BuildChannel(resource, now);
        for (var index = 0; index < channels.Count; index++)
        {
            var row = channels[index];
            if (row.ResourceKey is not { Length: > 0 } key || !string.Equals(key, resource.Path, StringComparison.Ordinal))
            {
                continue;
            }

            // Same identity, fresh facts: the id, the origin and the user's pin survive; the addresses
            // and the lifetime follow the document. AddedAt is the row's own history and stays.
            channels[index] = refreshed with
            {
                Id = row.Id,
                SortIndex = row.SortIndex,
                Pinned = row.Pinned,
                AddedAt = row.AddedAt,
                LastPlayedAt = row.LastPlayedAt,
                LastPlayOutcome = row.LastPlayOutcome,
                LastPlayOutcomeAt = row.LastPlayOutcomeAt
            };
            return new BroadcastChannelApplyResult(channels, channels[index], Added: false);
        }

        channels.Add(refreshed);
        return new BroadcastChannelApplyResult(channels, refreshed, Added: true);
    }
}
