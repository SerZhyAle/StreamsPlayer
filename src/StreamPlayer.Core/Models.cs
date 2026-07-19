namespace StreamPlayer.Core;

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

public enum CatalogViewMode
{
    List,
    Grid
}

public enum AppLanguage
{
    English,
    Russian
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
    int? FaviconIndex);

public sealed record StreamBank(
    IReadOnlyList<CatalogEntry> Entries,
    byte[]? FaviconAtlas,
    bool CsvWasFirstEntry,
    int? MaximumFaviconIndex);

public sealed record CatalogState
{
    public int SchemaVersion { get; init; } = 1;
    public List<StreamChannel> Channels { get; init; } = [];
    public string? AtlasFileName { get; init; }
    public DateTimeOffset? LastCatalogRefreshAt { get; init; }
    public CatalogViewMode ViewMode { get; init; }
    public AppLanguage Language { get; init; }
    public bool MainWindowTopmost { get; init; }
    public bool PlayerWindowTopmost { get; init; }
    public int VideoVolume { get; init; } = 100;
    public bool VideoMuted { get; init; }
    public StreamTileSize TileSize { get; init; } = StreamTileSize.Medium;
    public bool UpdateStreamPreviews { get; init; } = true;
    public Guid? LastSelectedChannelId { get; init; }
    public string CatalogSearchQuery { get; init; } = string.Empty;
    public string CatalogMediaFilter { get; init; } = "All";
    public string CatalogCategoryFilter { get; init; } = "All";
    public string CatalogLanguageFilter { get; init; } = "All";
    public string CatalogCountryFilter { get; init; } = "All";
    public string CatalogSortMode { get; init; } = "Name";
    public Guid? CatalogScrollAnchorId { get; init; }
}

public sealed record MergeResult(
    IReadOnlyList<StreamChannel> Channels,
    int Added,
    int Updated,
    int Removed);

public sealed record CatalogRefreshResult(
    CatalogState State,
    int Added,
    int Updated,
    int Removed);
