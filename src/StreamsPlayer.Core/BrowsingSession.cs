namespace StreamsPlayer.Core;

/// <summary>
/// Where the user is in the catalog: the search text, the facet filters, the sort order, the scroll
/// position and the last highlighted channel (SP-0067).
/// </summary>
/// <remarks>
/// These eleven short values used to live on <see cref="CatalogState"/> beside the channel list, so
/// pausing a scroll or clicking a card serialized the whole catalog - 15.15 MB and up to 377 ms on the
/// owner's 19 855 channels, measured before the split. They change constantly and they are worth
/// nothing if lost, which is the opposite of everything else in the state file; they get their own.
/// <para>
/// <see cref="ScrollOffset"/> is a pixel offset, not a channel identity (SP-0067 settled question 1).
/// Reading it costs one number instead of a walk over every row, and the accepted consequence is that a
/// restore after a filter or width change lands near the previous position rather than on it. It is a
/// <see cref="double"/> and every filter is a string precisely so no WPF type reaches this library.
/// </para>
/// </remarks>
public sealed record BrowsingSession
{
    /// <summary>
    /// Written so a later shape change can be told from a corrupt file. Nothing branches on it yet; a
    /// reader that finds an unknown version still gets every field it recognizes, because an unreadable
    /// session costs the saved filters and never the catalog.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    public string SearchQuery { get; init; } = string.Empty;
    public string MediaFilter { get; init; } = "All";
    public string CategoryFilter { get; init; } = "All";

    /// <summary>Rubric facet (SP-0061), stored as the catalog's identifier and never as a label.</summary>
    public string TopicFilter { get; init; } = "All";

    public string LanguageFilter { get; init; } = "All";
    public string CountryFilter { get; init; } = "All";
    public string MinBitrateFilter { get; init; } = "All";
    public string CollectionFilter { get; init; } = "All";
    public string SortMode { get; init; } = "Name";

    /// <summary>Vertical scroll offset of the catalog list, in device-independent pixels.</summary>
    public double ScrollOffset { get; init; }

    /// <summary>
    /// The last highlighted row. Written by selection only - since SP-0062 withdrew play-on-launch,
    /// nothing starts a stream from it, so it restores a highlight and nothing more.
    /// </summary>
    public Guid? LastSelectedChannelId { get; init; }
}
