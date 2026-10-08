using System.Net;

namespace StreamsPlayer.Core;

/// <summary>How a descriptor was applied to persisted channels.</summary>
public sealed record FastMediaSorterBroadcastApplyResult(
    IReadOnlyList<StreamChannel> Channels,
    StreamChannel Channel,
    bool Added);

/// <summary>
/// Applies a validated broadcast descriptor without I/O. Matching by <c>sourceId</c> first is what lets a
/// device advertise a new DHCP address or watch port without shedding the user's channel identity.
/// </summary>
public static class FastMediaSorterBroadcastImport
{
    /// <summary>
    /// Applies a validated broadcast descriptor without I/O. Matching by <c>sourceId</c> first is what lets a
    /// device advertise a new DHCP address or watch port without shedding the user's channel identity.
    /// </summary>
    /// <remarks>
    /// <para>SP-0201: a descriptor that arrived through the exchange directory passes
    /// <paramref name="directoryBroadcastId"/>. The directory's one act on a row it did not create is the
    /// <c>sourceId</c> replacement, so a replaced row keeps the origin it had: a manual, file or link row
    /// never becomes a directory row and can therefore never be marked ended by a record leaving. A row the
    /// directory itself created keeps its mark through the replacement and is revived - a descriptor this
    /// fresh says the broadcast is live again.</para>
    /// <para><paramref name="addWhenMissing"/> is false only for a directory push (SP-0201 open question 1,
    /// Option A): a background frame may refresh what the user already kept, never add to the library.</para>
    /// </remarks>
    public static FastMediaSorterBroadcastApplyResult Apply(
        IEnumerable<StreamChannel> existingChannels,
        FastMediaSorterBroadcast descriptor,
        DateTimeOffset now,
        string? directoryBroadcastId = null,
        bool addWhenMissing = true)
    {
        ArgumentNullException.ThrowIfNull(existingChannels);
        ArgumentNullException.ThrowIfNull(descriptor);

        var channels = existingChannels.ToList();
        // SP-0159: a video descriptor selects its declared RTSP endpoint; the kind follows the address,
        // so a camera broadcast lands where the catalog's own RTSP rows land.
        var endpoint = descriptor.SelectPlaybackEndpoint();
        var existing = FindExisting(channels, descriptor.SourceId, endpoint.Url);
        var previous = existing?.FastMediaSorterBroadcast;
        // The origin is a fact about the row, not about the frame. A row the directory created follows the
        // record that replaces it (its broadcastId moves with the broadcast); a row the user made by hand
        // never gains the mark - requirement 4's "never marked ended" holds because such a row never becomes
        // a directory row, not because the marking step looks elsewhere.
        string? origin = existing is null
            ? directoryBroadcastId
            : previous?.DirectoryBroadcastId is { } kept ? directoryBroadcastId ?? kept : null;
        var broadcast = new FastMediaSorterBroadcastInfo
        {
            SourceId = descriptor.SourceId,
            Mode = descriptor.Mode,
            SelectedTransport = endpoint.Transport,
            Endpoints = descriptor.Endpoints,
            TargetLatencyMs = endpoint.TargetLatencyMs ?? descriptor.TargetLatencyMs,
            DirectoryBroadcastId = origin,
            DirectoryEndedAt = null
        };

        if (existing is not null)
        {
            var replacement = existing with
            {
                Url = endpoint.Url,
                Title = string.IsNullOrWhiteSpace(descriptor.Title) ? existing.Title : descriptor.Title,
                MediaKind = ClassifyKind(descriptor, endpoint),
                IsLive = true,
                FastMediaSorterBroadcast = broadcast
            };
            var index = channels.FindIndex(channel => channel.Id == existing.Id);
            channels[index] = replacement;
            return new(channels, replacement, Added: false);
        }

        if (!addWhenMissing)
        {
            throw new InvalidOperationException("A directory push may only replace a stored channel.");
        }

        var title = string.IsNullOrWhiteSpace(descriptor.Title) ? TitleFrom(endpoint.Url) : descriptor.Title;
        var nextOrder = channels.Count == 0 ? 0 : channels.Max(channel => channel.SortIndex) + 1;
        var added = new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = endpoint.Url,
            Title = title,
            MediaKind = ClassifyKind(descriptor, endpoint),
            SourceOrigin = SourceOrigin.Imported,
            SortIndex = nextOrder,
            AddedAt = now,
            IsLive = true,
            FastMediaSorterBroadcast = broadcast
        };
        channels.Add(added);
        return new(channels, added, Added: true);
    }

    /// <summary>
    /// SP-0201 Option A: a directory push replaces a stored channel with the same <c>sourceId</c> (or a
    /// directory row with the same address) and adds nothing - <c>null</c> is the push that found no row.
    /// The producer's title is dropped on purpose: the row keeps the name the user gave it, and the
    /// directory's own name for the broadcast is what the live view shows (SP-0201 requirement 4).
    /// </summary>
    public static FastMediaSorterBroadcastApplyResult? ApplyStored(
        IEnumerable<StreamChannel> existingChannels,
        FastMediaSorterBroadcast descriptor,
        DateTimeOffset now,
        string broadcastId)
    {
        var channels = existingChannels.ToList();
        var endpoint = descriptor.SelectPlaybackEndpoint();
        return FindExisting(channels, descriptor.SourceId, endpoint.Url) is null
            ? null
            : Apply(channels, descriptor with { Title = string.Empty }, now, broadcastId, addWhenMissing: false);
    }

    /// <summary>Whether the channel is live for playback semantics, independent of catalog provenance.</summary>
    public static bool IsLive(StreamChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.FastMediaSorterBroadcast is not null || channel.IsLive == true || IsKnownAddress(channel.Url);
    }

    /// <summary>Whether the channel is safe to route through the FastMediaSorter-specific playback path.</summary>
    public static bool IsFastMediaSorterBroadcast(StreamChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.FastMediaSorterBroadcast is not null || IsKnownAddress(channel.Url);
    }

    /// <summary>
    /// The shipped phone/watch paths are recognisable only on a LAN IPv4 HTTP address. A generic manual
    /// HTTP URL does not become a FastMediaSorter source merely because it was labelled live by its user.
    /// </summary>
    public static bool IsKnownAddress(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(uri.Host, out var address) || address.GetAddressBytes().Length != 4)
        {
            return false;
        }

        return uri.AbsolutePath.Equals("/live-audio.aac", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.Equals("/live-audio", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.Equals("/listen", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// S9-3: a row that came from a bank belongs to the catalog merge, not to a hand-off - rewriting its
    /// address or title in place would be undone or contradicted by the next refresh (and would clobber the
    /// published metadata). Such a row is never matched; the broadcast lands as its own imported row and the
    /// merge already treats a user twin of a catalog URL as a separate row (SP-0177).
    /// </summary>
    private static bool IsReplaceable(StreamChannel channel) =>
        channel.SourceOrigin is not (SourceOrigin.Catalog or SourceOrigin.LocalCatalog);

    private static StreamChannel? FindExisting(
        IEnumerable<StreamChannel> channels,
        string? sourceId,
        string url)
    {
        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            var bySourceId = channels.FirstOrDefault(channel =>
                IsReplaceable(channel) &&
                string.Equals(channel.FastMediaSorterBroadcast?.SourceId, sourceId, StringComparison.Ordinal));
            if (bySourceId is not null)
            {
                return bySourceId;
            }
        }

        return channels.FirstOrDefault(channel =>
            IsReplaceable(channel) && CatalogUrlIdentity.SameIdentity(channel.Url, url));
    }

    /// <summary>
    /// The row's kind follows the descriptor's mode, not the URL heuristic alone (SP-0203): a video
    /// broadcast whose representative address is a relay `/stream` or token path still plays in the
    /// player window, so it must not land as an audio row. RTSP keeps its own kind; an audio
    /// descriptor stays audio whatever the address looks like.
    /// </summary>
    private static MediaKind ClassifyKind(FastMediaSorterBroadcast descriptor, FastMediaSorterBroadcastEndpoint endpoint)
    {
        var kind = StreamMediaKindClassifier.Classify(endpoint.Url);
        return descriptor.IsVideoMode && kind == MediaKind.Audio ? MediaKind.Video : kind;
    }

    private static string TitleFrom(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
}
