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
    /// <see cref="StreamChannel.ResourceKey"/> matches the resource's path is UPDATED in place (same id, so
    /// pins, collections and history reattach by themselves, and the user's title and metadata stay; only the
    /// addresses, lifetime, playlist flag and access note follow the document); anything else ADDS one row at the end.
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

            // S14-3: start from the row, not from the document. The row is the user's (an edit does not make
            // an Imported row Manual, so nothing else protects it): its category, topic, language, country,
            // homepage, format, pin, history and origin all stay. Only what the document owns follows it: the
            // name (a re-import is the person asking for the refreshed document), the addresses, the lifetime,
            // the playlist flag and the access note.
            channels[index] = row with
            {
                // A nameless document builds the "Broadcast" placeholder: never let it replace a real or edited title.
                Title = string.IsNullOrWhiteSpace(resource.ResourceName) ? row.Title : refreshed.Title,
                // The document declares audio or video; a path reused for the other kind must route to the right player.
                MediaKind = refreshed.MediaKind,
                Url = refreshed.Url,
                AlternateUrls = refreshed.AlternateUrls,
                ResourceExpiresAt = refreshed.ResourceExpiresAt,
                IsPlaylistResource = refreshed.IsPlaylistResource,
                ResourceAccessNote = refreshed.ResourceAccessNote
            };
            return new BroadcastChannelApplyResult(channels, channels[index], Added: false);
        }

        channels.Add(refreshed);
        return new BroadcastChannelApplyResult(channels, refreshed, Added: true);
    }
}
