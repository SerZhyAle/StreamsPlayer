namespace StreamsPlayer.Core;

/// <summary>
/// SP-0086: the set a random radio station may be drawn from, and the draw itself.
/// </summary>
/// <remarks>
/// The membership rule lives here rather than inline at the call site because three of the ticket's
/// acceptance criteria are properties of the set, not of the loop that consumes it: a hidden station is
/// never offered, video and RTSP are never offered, and a row whose URL cannot be launched never costs
/// an attempt. Those are testable here and untestable inside a menu handler.
/// </remarks>
public static class RandomStationSelection
{
    /// <summary>
    /// Every channel the random command is allowed to offer: audio, launchable, and not hidden.
    /// </summary>
    /// <remarks>
    /// Hiding is catalog-only, matching the rule the list view applies: a <see cref="SourceOrigin.Manual"/>
    /// or <see cref="SourceOrigin.Imported"/> row that happens to share a URL with a hidden catalog row is
    /// the user's own row and stays eligible. Ordering is the caller's ordering, preserved - the draw is
    /// what introduces randomness, and a selection step that also shuffled would make both untestable.
    /// </remarks>
    public static List<StreamChannel> Eligible(IReadOnlyList<StreamChannel> channels, IReadOnlyCollection<string> hiddenCatalogUrls)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(hiddenCatalogUrls);

        var hidden = hiddenCatalogUrls.Count == 0
            ? null
            : new HashSet<string>(hiddenCatalogUrls.Select(CatalogUrlIdentity.Normalize), StringComparer.Ordinal);

        var eligible = new List<StreamChannel>();
        foreach (var channel in channels)
        {
            if (channel.MediaKind != MediaKind.Audio ||
                !StreamMediaKindClassifier.IsLaunchable(channel.Url))
            {
                continue;
            }

            if (hidden is not null &&
                channel.SourceOrigin == SourceOrigin.Catalog &&
                hidden.Contains(CatalogUrlIdentity.Normalize(channel.Url)))
            {
                continue;
            }

            // SP-0089: a retired row is user data the application is keeping alive, not a station on
            // offer. Drawing one would be the application volunteering a channel the bank has stopped
            // publishing - the opposite of what "surprise me" asks for, and the likeliest one to be dead.
            if (channel.RetiredAt is not null)
            {
                continue;
            }

            eligible.Add(channel);
        }

        return eligible;
    }

    /// <summary>One independent draw, or <c>null</c> when there is nothing to draw from.</summary>
    /// <remarks>
    /// The generator is the caller's, which is the only thing that makes the distribution testable.
    /// <see cref="Random"/> and not <see cref="System.Security.Cryptography.RandomNumberGenerator"/>: this
    /// picks a radio station, not a key. There is deliberately no exclusion parameter and no memory of
    /// past draws - each press is an independent roll, and the consequence of drawing the station already
    /// playing is handled where playback is, not by biasing the draw.
    /// </remarks>
    public static StreamChannel? Draw(IReadOnlyList<StreamChannel> eligible, Random random)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(random);

        return eligible.Count == 0 ? null : eligible[random.Next(eligible.Count)];
    }
}
