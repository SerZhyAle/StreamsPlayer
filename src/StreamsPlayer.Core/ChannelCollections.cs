namespace StreamsPlayer.Core;

/// <summary>
/// SP-0017: local named collections. A collection is an ordered list of channel ids and nothing
/// else - deleting a collection never touches a channel, and pinning is a separate mechanism that
/// this file deliberately knows nothing about.
///
/// Membership is by channel id, not URL: an explicit catalog refresh updates surviving rows in
/// place (same id), so memberships survive it, while genuinely pruned rows are dropped by
/// <see cref="Prune"/>. Every operation is pure: it returns a new list or null when the request is
/// invalid, so the App can persist the result atomically or show a message without half-applying.
/// </summary>
public static class ChannelCollections
{
    public const int MaximumNameLength = 40;

    /// <summary>Trimmed, whitespace-collapsed display name, or null when there is nothing left.</summary>
    public static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var collapsed = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > MaximumNameLength ? collapsed[..MaximumNameLength] : collapsed;
    }

    /// <summary>Case-insensitive uniqueness check; <paramref name="exceptId"/> lets a rename keep its own name.</summary>
    public static bool IsNameAvailable(IEnumerable<ChannelCollection> collections, string? name, Guid? exceptId = null)
    {
        var normalized = NormalizeName(name);
        return normalized is not null &&
               !collections.Any(collection =>
                   collection.Id != exceptId &&
                   string.Equals(collection.Name, normalized, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Appends a new empty collection, or returns null when the name is blank or taken.</summary>
    public static IReadOnlyList<ChannelCollection>? Create(
        IReadOnlyList<ChannelCollection> collections,
        string? name,
        Guid id)
    {
        var normalized = NormalizeName(name);
        if (normalized is null || !IsNameAvailable(collections, normalized))
        {
            return null;
        }

        return [.. collections, new ChannelCollection { Id = id, Name = normalized }];
    }

    public static IReadOnlyList<ChannelCollection>? Rename(
        IReadOnlyList<ChannelCollection> collections,
        Guid id,
        string? name)
    {
        var normalized = NormalizeName(name);
        if (normalized is null ||
            !collections.Any(collection => collection.Id == id) ||
            !IsNameAvailable(collections, normalized, id))
        {
            return null;
        }

        return [.. collections.Select(collection =>
            collection.Id == id ? collection with { Name = normalized } : collection)];
    }

    /// <summary>Removes the collection. The channels it referenced are untouched.</summary>
    public static IReadOnlyList<ChannelCollection> Delete(IReadOnlyList<ChannelCollection> collections, Guid id) =>
        [.. collections.Where(collection => collection.Id != id)];

    /// <summary>
    /// Adds a channel to the end of one collection. Membership is a set per collection: adding twice
    /// is a no-op that keeps the existing position rather than reordering the user's list.
    /// </summary>
    public static IReadOnlyList<ChannelCollection> AddChannel(
        IReadOnlyList<ChannelCollection> collections,
        Guid collectionId,
        Guid channelId) =>
        [.. collections.Select(collection =>
            collection.Id == collectionId && !collection.ChannelIds.Contains(channelId)
                ? collection with { ChannelIds = [.. collection.ChannelIds, channelId] }
                : collection)];

    public static IReadOnlyList<ChannelCollection> RemoveChannel(
        IReadOnlyList<ChannelCollection> collections,
        Guid collectionId,
        Guid channelId) =>
        [.. collections.Select(collection =>
            collection.Id == collectionId
                ? collection with { ChannelIds = [.. collection.ChannelIds.Where(id => id != channelId)] }
                : collection)];

    /// <summary>Drops a deleted channel from every collection; the collections themselves survive, even if empty.</summary>
    public static IReadOnlyList<ChannelCollection> RemoveChannelEverywhere(
        IReadOnlyList<ChannelCollection> collections,
        Guid channelId) =>
        [.. collections.Select(collection =>
            collection.ChannelIds.Contains(channelId)
                ? collection with { ChannelIds = [.. collection.ChannelIds.Where(id => id != channelId)] }
                : collection)];

    /// <summary>
    /// Removes references to channels that no longer exist (a catalog refresh pruned them, or a user
    /// row was deleted while the app was closed). Order of the survivors is preserved and empty
    /// collections stay - only the user deletes a collection.
    /// </summary>
    public static IReadOnlyList<ChannelCollection> Prune(
        IReadOnlyList<ChannelCollection> collections,
        IEnumerable<Guid> existingChannelIds)
    {
        var alive = existingChannelIds as HashSet<Guid> ?? [.. existingChannelIds];
        return [.. collections.Select(collection =>
            collection.ChannelIds.All(alive.Contains)
                ? collection
                : collection with { ChannelIds = [.. collection.ChannelIds.Where(alive.Contains)] })];
    }

    /// <summary>Members of a collection in its saved order; ids without a live channel are skipped.</summary>
    public static IReadOnlyList<StreamChannel> Members(
        ChannelCollection collection,
        IEnumerable<StreamChannel> channels)
    {
        var byId = channels.ToDictionary(channel => channel.Id);
        return [.. collection.ChannelIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])];
    }

    /// <summary>Ids of the collections a channel belongs to, in collection order.</summary>
    public static IReadOnlyList<Guid> MembershipOf(
        IEnumerable<ChannelCollection> collections,
        Guid channelId) =>
        [.. collections.Where(collection => collection.ChannelIds.Contains(channelId)).Select(collection => collection.Id)];
}
