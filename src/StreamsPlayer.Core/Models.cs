namespace StreamsPlayer.Core;

public enum MediaKind
{
    Audio,
    Video,
    Rtsp
}

public enum SourceOrigin
{
    Catalog,
    Manual,
    Imported
}

public enum PlayOutcome
{
    Ok,
    Fail
}

// SP-0033: the catalog's `access` column. `Open` is first so it is the value an older state file and
// any unrecognised future upstream token both land on - an unknown tag must render as nothing rather
// than leak a machine value into the UI.
public enum ChannelAccess
{
    Open,
    GeoRestricted
}

public enum CatalogViewMode
{
    List,
    Grid
}

// SP-0029: persisted by name via JsonStringEnumConverter, so an older state file that predates
// Ukrainian still round-trips and new values can be appended without a migration.
public enum AppLanguage
{
    English,
    Russian,
    Ukrainian
}

public enum StreamLaunchTargetKind
{
    None,
    Url,
    ChannelId,
    Invalid
}

public sealed record StreamLaunchRequest(
    StreamLaunchTargetKind Kind,
    string? Url = null,
    Guid? ChannelId = null)
{
    public static StreamLaunchRequest Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return new(StreamLaunchTargetKind.None);
        }

        if (arguments.Count != 2)
        {
            return new(StreamLaunchTargetKind.Invalid);
        }

        var option = arguments[0];
        var value = arguments[1].Trim();
        if (option.Equals("--url", StringComparison.OrdinalIgnoreCase) &&
            StreamMediaKindClassifier.IsLaunchable(value))
        {
            return new(StreamLaunchTargetKind.Url, Url: value);
        }

        if (option.Equals("--id", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(value, out var channelId))
        {
            return new(StreamLaunchTargetKind.ChannelId, ChannelId: channelId);
        }

        return new(StreamLaunchTargetKind.Invalid);
    }
}

public enum StreamTileSize
{
    Small,
    Medium,
    Large
}

public enum MediaBackend
{
    LibVlc,
    Flyleaf
}

public sealed record StreamChannel
{
    public required Guid Id { get; init; }
    public required string Url { get; init; }
    public required string Title { get; init; }
    public required MediaKind MediaKind { get; init; }
    public required SourceOrigin SourceOrigin { get; init; }
    public long SortIndex { get; init; }
    public bool Pinned { get; init; }
    public required DateTimeOffset AddedAt { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
    public string? Category { get; init; }
    public string? Topic { get; init; }
    public string? Language { get; init; }
    public string? Country { get; init; }
    public string? Homepage { get; init; }
    public PlayOutcome? LastPlayOutcome { get; init; }
    public DateTimeOffset? LastPlayOutcomeAt { get; init; }
    public int? FaviconIndex { get; init; }

    // Optional, untrusted maintainer metadata from the catalog (SP-0018). Bitrate is the raw
    // claim string; numeric interpretation goes through StreamBitrate. Never gate default
    // visibility on these, and never infer a playback decision or success mark from them.
    public string? Protocol { get; init; }
    public string? Format { get; init; }
    public string? Bitrate { get; init; }
    public bool? IsLive { get; init; }

    // SP-0033: region-restriction heuristic observed from the catalog maintainer's network only. A
    // GeoRestricted channel is deliberately kept and stays fully playable - never gate, reorder, or
    // hide on this, and never turn it into a playback decision or a failure verdict.
    public ChannelAccess Access { get; init; } = ChannelAccess.Open;
}

public sealed record CatalogEntry(
    string Title,
    string Url,
    MediaKind MediaKind,
    string? Category,
    string? Topic,
    string? Language,
    string? Country,
    string? Homepage,
    int? FaviconIndex,
    string? Protocol = null,
    string? Format = null,
    string? Bitrate = null,
    bool? IsLive = null,
    ChannelAccess Access = ChannelAccess.Open);

public sealed record StreamBank(
    IReadOnlyList<CatalogEntry> Entries,
    byte[]? FaviconAtlas,
    bool CsvWasFirstEntry,
    int? MaximumFaviconIndex);

/// <summary>
/// One channel in the local listening history (SP-0019). Keyed by <see cref="ChannelId"/>; a replay
/// updates the existing entry rather than adding a row. No URL is stored: playback resolves the id
/// against the current catalog only, so a deleted channel stays a non-playable label and is never
/// reopened from a stale address. <see cref="LastTrackText"/> is the last observed ICY now-playing
/// text (SP-0014) - a best-effort display string, not verified track identity.
/// </summary>
public sealed record ListeningHistoryEntry
{
    public required Guid ChannelId { get; init; }
    public required string Title { get; init; }
    public required MediaKind MediaKind { get; init; }
    public required DateTimeOffset LastPlayedAt { get; init; }
    public string? LastTrackText { get; init; }
}

/// <summary>
/// One local named collection (SP-0017): an ordered list of channel ids under a user-chosen name.
/// Membership is many-to-many and each collection keeps its own order, so the same channel can sit
/// first in one collection and last in another. An older state file without collections
/// deserializes to the empty default.
/// </summary>
public sealed record ChannelCollection
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public List<Guid> ChannelIds { get; init; } = [];
}

public sealed record CatalogState
{
    public int SchemaVersion { get; init; } = 1;
    public List<StreamChannel> Channels { get; init; } = [];

    /// <summary>Local named collections (SP-0017). Never uploaded; deleting one never deletes channels.</summary>
    public List<ChannelCollection> Collections { get; init; } = [];

    /// <summary>
    /// Local listening history (SP-0019), most-recent-first, keyed by channel id and bounded to
    /// <see cref="StreamsPlayer.Core.ListeningHistory.MaxEntries"/>. Private application data: never
    /// uploaded, synchronized, or shared, and cleared only by explicit user action. An older state
    /// file lacking this key deserializes to the empty default.
    /// </summary>
    public List<ListeningHistoryEntry> ListeningHistory { get; init; } = [];

    /// <summary>
    /// Normalized URL identities of catalog channels the user chose to hide. Persisted so an explicit
    /// catalog refresh (which re-adds catalog rows by URL) does not bring a hidden channel back.
    /// Only <see cref="SourceOrigin.Catalog"/> rows are ever hidden; user rows are deleted instead.
    /// </summary>
    public List<string> HiddenCatalogUrls { get; init; } = [];
    public string? AtlasFileName { get; init; }
    public DateTimeOffset? LastCatalogRefreshAt { get; init; }
    public CatalogViewMode ViewMode { get; init; }
    public AppLanguage Language { get; init; }
    public bool MainWindowTopmost { get; init; }
    public bool PlayerWindowTopmost { get; init; }
    public int VideoVolume { get; init; } = 100;
    public bool VideoMuted { get; init; }
    public int AudioVolume { get; init; } = 100;

    /// <summary>
    /// When true (default), the app holds a Windows power request while a stream is actively
    /// playing so the machine's idle-sleep timer does not cut a long session short. Only the
    /// idle-sleep timer is affected; explicit user sleep, hibernate, and lid-close are never
    /// overridden. Defaults on for pre-existing state: an older state file lacking this key
    /// deserializes to the initializer default.
    /// </summary>
    public bool KeepAwakeDuringPlayback { get; init; } = true;

    /// <summary>
    /// When true, the active inline audio session is published to the Windows System Media
    /// Transport Controls so the media flyout and hardware media keys can drive Play/Pause,
    /// Stop, and Previous/Next (SP-0021). Defaults off: an older state file lacking this key
    /// deserializes to the initializer default, preserving the pre-feature behaviour.
    /// </summary>
    public bool SystemMediaControls { get; init; }
    public StreamTileSize TileSize { get; init; } = StreamTileSize.Medium;
    public bool UpdateStreamPreviews { get; init; } = true;

    /// <summary>
    /// Element revision of the published channel-preview atlas already seeded into the local preview
    /// store (SP-0031), or null when it never has been. Equal to
    /// <see cref="ChannelPreviewAtlasService.Revision"/> means "seeded, do not offer"; a different value
    /// means the publisher shipped a new, tile-incompatible sheet and the offer becomes eligible again.
    /// Written only after a successful import, so a failed or declined one always leaves the offer open.
    /// </summary>
    public string? ChannelPreviewAtlasRevision { get; init; }

    /// <summary>
    /// Playback engine for the video/RTSP player window only (SP-0026). Defaults to
    /// <see cref="MediaBackend.LibVlc"/> - the proven baseline; <see cref="MediaBackend.Flyleaf"/>
    /// is an opt-in troubleshooting fallback. Audio and headless thumbnail capture ignore this.
    /// An older state file lacking this key deserializes to the LibVlc default.
    /// </summary>
    public MediaBackend VideoBackend { get; init; } = MediaBackend.LibVlc;
    public Guid? LastSelectedChannelId { get; init; }
    public string CatalogSearchQuery { get; init; } = string.Empty;
    public string CatalogMediaFilter { get; init; } = "All";
    public string CatalogCategoryFilter { get; init; } = "All";
    public string CatalogLanguageFilter { get; init; } = "All";
    public string CatalogCountryFilter { get; init; } = "All";

    /// <summary>
    /// Minimum-bitrate filter (SP-0018). "All" (default) leaves the catalog view unchanged; a numeric
    /// kbps threshold excludes rows whose advertised bitrate is missing or cannot be interpreted.
    /// An older state file lacking this key deserializes to the initializer default.
    /// </summary>
    public string CatalogMinBitrateFilter { get; init; } = "All";
    /// <summary>
    /// Active collection view (SP-0017): "All" (default) or a collection id. A stale id - the
    /// collection was deleted while the app was closed - falls back to "All" instead of an empty list.
    /// </summary>
    public string CatalogCollectionFilter { get; init; } = "All";
    public string CatalogSortMode { get; init; } = "Name";
    public Guid? CatalogScrollAnchorId { get; init; }

    /// <summary>
    /// Collapsed/expanded state of the two catalog sections (SP-0025). Both default to expanded;
    /// an older state file lacking these keys deserializes to the initializer default (false).
    /// </summary>
    public bool PinnedSectionCollapsed { get; init; }
    public bool MainSectionCollapsed { get; init; }
}

public sealed record MergeResult(
    IReadOnlyList<StreamChannel> Channels,
    int Added,
    int Updated,
    int Removed);

/// <summary>
/// Outcome of <see cref="CatalogPurge.RemoveDownloaded"/> (SP-0030): the state without downloaded
/// rows, and the ids that left so the UI can release their cached rows, selection, and playback.
/// </summary>
public sealed record CatalogPurgeResult(
    CatalogState State,
    IReadOnlyList<Guid> RemovedChannelIds);

public sealed record CatalogRefreshResult(
    CatalogState State,
    int Added,
    int Updated,
    int Removed);
