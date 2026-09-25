namespace StreamsPlayer.Core;

/// <summary>
/// SP-0127: the command-line arguments a desktop shortcut or a copied launch command carries for a saved
/// channel, and how a parsed launch finds its channel again.
/// </summary>
/// <remarks>
/// <para>The channel's id alone is not durable. A refresh deletes a catalog row the bank stops listing
/// unless the user authored something on it, and a returning address gets a new id - so a shortcut to an
/// unpinned channel broke for good the first time the bank dropped it. The address rides beside the id,
/// and a launch whose id is gone falls back to it: no state has to be kept in step with files the user
/// may delete from the desktop, and an address was already a supported launch argument.</para>
/// <para>The address is left out, never quoted around, when it could not survive the command line
/// intact: a quote or whitespace would split or end the argument, and a backslash before the closing
/// quote would escape it. It is also left out when it would push the arguments past what a shortcut can
/// store. Such a channel launches by id alone, exactly as every shortcut did before.</para>
/// </remarks>
public static class StreamLaunchArguments
{
    /// <summary>
    /// A shell link stores at most <c>INFOTIPSIZE</c> (1024) characters of arguments; the margin keeps the
    /// terminating NUL and any rounding in the shell's own accounting out of the question.
    /// </summary>
    public const int MaximumLength = 1000;

    /// <summary>The argument string for <paramref name="channel"/>, ready for a shortcut or a command line.</summary>
    public static string For(StreamChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var idOnly = $"--id \"{channel.Id:D}\"";
        if (!CanCarry(channel.Url))
        {
            return idOnly;
        }

        var withAddress = $"{idOnly} --url \"{channel.Url.Trim()}\"";
        return withAddress.Length <= MaximumLength ? withAddress : idOnly;
    }

    /// <summary>
    /// Whether an existing shortcut's <paramref name="arguments"/> start <paramref name="channelId"/> - the
    /// test that lets a shortcut be rewritten for its own channel but never taken over by another one.
    /// </summary>
    public static bool Names(string? arguments, Guid channelId) =>
        !string.IsNullOrEmpty(arguments) &&
        arguments.Contains(channelId.ToString("D"), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The saved channel a <see cref="StreamLaunchTargetKind.ChannelId"/> launch names: the row with that
    /// id, otherwise the row at the launch's fallback address, otherwise <see langword="null"/>. The caller
    /// decides what an unresolved launch with an address does - it can still play that address.
    /// </summary>
    public static StreamChannel? Resolve(IEnumerable<StreamChannel> channels, StreamLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind != StreamLaunchTargetKind.ChannelId)
        {
            return null;
        }

        var list = channels as IReadOnlyCollection<StreamChannel> ?? channels.ToList();
        var byId = list.FirstOrDefault(channel => channel.Id == request.ChannelId);
        if (byId is not null || string.IsNullOrWhiteSpace(request.Url))
        {
            return byId;
        }

        var address = CatalogUrlIdentity.Normalize(request.Url);
        var matches = list.Where(channel => string.Equals(CatalogUrlIdentity.Normalize(channel.Url), address, StringComparison.Ordinal));

        // A retired row keeps the user's pin and collections, but a live one is what the bank offers now.
        return matches.FirstOrDefault(channel => channel.RetiredAt is null) ?? matches.FirstOrDefault();
    }

    private static bool CanCarry(string? url) =>
        LaunchableAddress.IsLaunchable(url) &&
        !url!.Trim().Any(character => character is '"' or '\\' || char.IsWhiteSpace(character) || char.IsControl(character));
}
