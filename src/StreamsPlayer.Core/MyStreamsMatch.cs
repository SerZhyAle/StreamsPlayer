using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0233: matching and deduplication rules for the unified "My streams" personal view.
/// Reconciles announced device broadcasts with saved library channels using stable source identity.
/// </summary>
public static class MyStreamsMatch
{
    /// <summary>
    /// Finds a saved channel corresponding to the announced broadcast, scoped to the current account context.
    /// Returns null if no saved channel matches or if the channel belongs to a different account.
    /// </summary>
    public static StreamChannel? FindMatchingSavedChannel(
        IEnumerable<StreamChannel> savedChannels,
        ExchangeBroadcastView view,
        string? ownAccountIdentity)
    {
        ArgumentNullException.ThrowIfNull(savedChannels);
        ArgumentNullException.ThrowIfNull(view);

        var descriptorSourceId = view.Record.Descriptor?.SourceId;
        var broadcastId = view.Record.BroadcastId;

        foreach (var channel in savedChannels)
        {
            var info = channel.FastMediaSorterBroadcast;
            if (info is null)
            {
                continue;
            }

            // Cross-account isolation (SP-0233 req 9 & 12): do not merge broadcasts from different accounts/servers.
            if (!string.IsNullOrEmpty(info.DirectoryEntryIdentity) &&
                !string.IsNullOrEmpty(ownAccountIdentity) &&
                !string.Equals(info.DirectoryEntryIdentity, ownAccountIdentity, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Stable source identity takes precedence (SP-0233 req 9).
            if (!string.IsNullOrEmpty(descriptorSourceId) &&
                string.Equals(info.SourceId, descriptorSourceId, StringComparison.Ordinal))
            {
                return channel;
            }

            // Fallback to directory broadcast identity if sourceId is absent.
            if (!string.IsNullOrEmpty(broadcastId) &&
                string.Equals(info.DirectoryBroadcastId, broadcastId, StringComparison.Ordinal))
            {
                return channel;
            }
        }

        return null;
    }

    /// <summary>
    /// Collects the IDs of saved channels that are currently announced by an online device in the directory snapshot.
    /// These are omitted from the standalone saved channels section to guarantee "one broadcast, one choice".
    /// </summary>
    public static HashSet<Guid> CollectAnnouncedSavedChannelIds(
        ExchangeDirectorySnapshot? snapshot,
        IEnumerable<StreamChannel> savedChannels,
        string? ownAccountIdentity)
    {
        var announcedIds = new HashSet<Guid>();
        if (snapshot?.Groups is null)
        {
            return announcedIds;
        }

        var channelList = savedChannels as IReadOnlyList<StreamChannel> ?? savedChannels.ToList();

        foreach (var group in snapshot.Groups)
        {
            foreach (var broadcast in group.Broadcasts)
            {
                if (FindMatchingSavedChannel(channelList, broadcast, ownAccountIdentity) is { } matched)
                {
                    announcedIds.Add(matched.Id);
                }
            }
        }

        return announcedIds;
    }

    /// <summary>
    /// Checks whether a device broadcast item matches the search query.
    /// Matches against device name, broadcast title, mode, and matched saved channel metadata.
    /// </summary>
    public static bool MatchesSearch(
        string deviceName,
        ExchangeBroadcastView broadcast,
        StreamChannel? matchedSavedChannel,
        string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var trimmed = query.Trim();

        if (deviceName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(broadcast.Record.Title) &&
            broadcast.Record.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (broadcast.Record.Mode.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (matchedSavedChannel is not null)
        {
            if (matchedSavedChannel.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(matchedSavedChannel.Category) &&
                matchedSavedChannel.Category.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(matchedSavedChannel.Topic) &&
                matchedSavedChannel.Topic.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
