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
    public static FastMediaSorterBroadcastApplyResult Apply(
        IEnumerable<StreamChannel> existingChannels,
        FastMediaSorterBroadcast descriptor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(existingChannels);
        ArgumentNullException.ThrowIfNull(descriptor);

        var channels = existingChannels.ToList();
        var endpoint = descriptor.SelectAudioEndpoint();
        var broadcast = new FastMediaSorterBroadcastInfo
        {
            SourceId = descriptor.SourceId,
            Mode = FastMediaSorterBroadcastDescriptor.AudioOnlyMode,
            SelectedTransport = endpoint.Transport,
            Endpoints = descriptor.Endpoints,
            TargetLatencyMs = endpoint.TargetLatencyMs ?? descriptor.TargetLatencyMs
        };

        var existing = FindExisting(channels, descriptor.SourceId, endpoint.Url);
        if (existing is not null)
        {
            var replacement = existing with
            {
                Url = endpoint.Url,
                Title = string.IsNullOrWhiteSpace(descriptor.Title) ? existing.Title : descriptor.Title,
                MediaKind = MediaKind.Audio,
                IsLive = true,
                FastMediaSorterBroadcast = broadcast
            };
            var index = channels.FindIndex(channel => channel.Id == existing.Id);
            channels[index] = replacement;
            return new(channels, replacement, Added: false);
        }

        var title = string.IsNullOrWhiteSpace(descriptor.Title) ? TitleFrom(endpoint.Url) : descriptor.Title;
        var nextOrder = channels.Count == 0 ? 0 : channels.Max(channel => channel.SortIndex) + 1;
        var added = new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = endpoint.Url,
            Title = title,
            MediaKind = MediaKind.Audio,
            SourceOrigin = SourceOrigin.Imported,
            SortIndex = nextOrder,
            AddedAt = now,
            IsLive = true,
            FastMediaSorterBroadcast = broadcast
        };
        channels.Add(added);
        return new(channels, added, Added: true);
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

    private static StreamChannel? FindExisting(
        IEnumerable<StreamChannel> channels,
        string? sourceId,
        string url)
    {
        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            var bySourceId = channels.FirstOrDefault(channel =>
                string.Equals(channel.FastMediaSorterBroadcast?.SourceId, sourceId, StringComparison.Ordinal));
            if (bySourceId is not null)
            {
                return bySourceId;
            }
        }

        return channels.FirstOrDefault(channel => CatalogUrlIdentity.SameIdentity(channel.Url, url));
    }

    private static string TitleFrom(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
}
