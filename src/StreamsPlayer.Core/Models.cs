using System.Text.Json.Serialization;

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

// SP-0052: which stored atlas a channel's FaviconIndex is an offset into. The index and the atlas that
// shipped with it are one pair, so once the bundled snapshot can add rows on top of a downloaded
// catalog, two atlases are installed at once and a row that does not name its own would render another
// channel's icon - a wrong picture, not a missing one. `Catalog` is first so an older state file, and
// any unreadable value, land on the source every row in such a file actually has.
public enum FaviconSource
{
    Catalog,
    Snapshot
}

// SP-0052: the atlas slot a save writes into. The two are independent: a save that replaces one never
// touches the other, and the store's cleanup keeps whichever files the saved state still names.
public enum AtlasSlot
{
    Catalog,
    Snapshot
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
// SP-0034: these member names are the persisted JSON tokens - never rename one, and keep English
// first so an unset value and a defaulted value agree. Per-surface codes, layout direction and
// culture matching live in InterfaceLanguages; this enum is only the identity.
public enum AppLanguage
{
    English,
    Russian,
    Ukrainian,
    German,
    Italian,
    Spanish,
    French,
    Portuguese,
    Chinese,
    Hindi,
    Bengali,
    Arabic,
    Urdu
}

// Persisted preference token. Keep System first: it is both the new-install default and the safe
// fallback for a state file written by a newer build.
public enum AppTheme
{
    System,
    Light,
    Dark
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
    Large,
    VerySmall
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

    // SP-0052: which installed atlas <see cref="FaviconIndex"/> indexes. Defaults to Catalog, so a state
    // file written before the bundled snapshot existed reads every row as what it is - a row whose icon
    // belongs to the downloaded atlas.
    public FaviconSource FaviconSource { get; init; } = FaviconSource.Catalog;

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

    /// <summary>
    /// SP-0089, source contract item D: when an explicit refresh found this row's URL missing from the
    /// bank while the user had authored something on it, or <c>null</c> while the bank still lists it.
    /// </summary>
    /// <remarks>
    /// <para>A row's absence from a bank build says "this build does not offer this channel". It does not
    /// say the user's pin, collection membership or history about it are invalid, and the producer's own
    /// incident of 2026-08-19 - 1 906 rows dropped, 79% of them on an <c>unknown</c> verdict and 1 321 of
    /// them demonstrably still playing the next day - is what turned that from a principle into a rule.
    /// Deletion there was unrecoverable in both directions: both sides keyed by absence and minted a new
    /// identity on return, so republishing the very same bytes restored no pin at all.</para>
    /// <para>The identity is the whole point of retiring rather than deleting. Collections and listening
    /// history reference <see cref="Id"/>, so the row surviving with its id is what makes the user's data
    /// reattach by itself when a later bank lists the URL again - at which point the merge clears this
    /// field and the channel is simply on offer once more. A row carrying nothing the user authored is
    /// still deleted outright; this field exists for the rows where deletion would destroy something.</para>
    /// <para>Retired means "kept, not offered": it stays where the user put it and leaves the general
    /// browse list, because a build that no longer publishes a channel must not have it presented as
    /// current. Absent from a state file written before SP-0089, which reads as null - correct, since
    /// every row in such a file was on offer at the last refresh.</para>
    /// </remarks>
    public DateTimeOffset? RetiredAt { get; init; }
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
/// The bundled stream bank together with the provenance that lets the interface say where the list came
/// from and how old it is (SP-0052). The date and the data live in the same archive precisely so they
/// cannot drift apart in the repository.
/// </summary>
public sealed record CatalogSnapshot(
    StreamBank Bank,
    DateTimeOffset SourceDate,
    string SourceUrl);

/// <summary>
/// Outcome of <see cref="CatalogSnapshotService.ApplyAsync"/>. There is no removal count: applying a
/// snapshot never removes (SP-0052 decision 4).
/// </summary>
public sealed record CatalogSnapshotApplyResult(
    CatalogState State,
    int Added,
    int Updated,
    DateTimeOffset SourceDate);

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

    /// <summary>
    /// The icon atlas that shipped with the bundled snapshot (SP-0052), installed into the state
    /// directory alongside the downloaded one, or <c>null</c> when the snapshot was never applied.
    /// Kept in its own slot because <see cref="StreamChannel.FaviconIndex"/> is only meaningful against
    /// the atlas of the same bank: one shared slot would make every applied snapshot either overwrite
    /// the downloaded atlas or be read against it, and both mis-render icons rather than omit them.
    /// </summary>
    public string? SnapshotAtlasFileName { get; init; }

    /// <summary>
    /// Source date of the bundled snapshot whose rows are in this state (SP-0052), or <c>null</c> when
    /// none was ever applied. Deliberately the date of the <em>data</em> rather than the moment it was
    /// applied: the interface has to say how old the list is, and a user who applied one build's
    /// snapshot and then updated the application would otherwise be shown the new build's date over
    /// the old build's channels. Applying a snapshot never writes
    /// <see cref="LastCatalogRefreshAt"/> - bundled data must not claim to be a download.
    /// </summary>
    public DateTimeOffset? AppliedSnapshotDate { get; init; }

    /// <summary>
    /// Whether the user declined the one first-launch offer to use the bundled snapshot (SP-0052), in
    /// which case it is never offered again. Persisted, unlike the channel-preview offer's per-session
    /// latch, because the offer appears at most once in the product's life; the settings action is the
    /// way back for a user who changes their mind. An older state file lacking this key deserializes
    /// to the initializer default and is offered normally.
    /// </summary>
    public bool CatalogSnapshotOfferDeclined { get; init; }
    public CatalogViewMode ViewMode { get; init; }

    /// <summary>
    /// The interface language the user chose, or <c>null</c> when they never chose one - in which case
    /// the app detects it from the operating system UI culture (SP-0034 decision 5).
    /// <para>
    /// Nullable because "unset" and "chose English" have to be distinguishable, and every build before
    /// SP-0034 serialized this property unconditionally, so an absent property really does mean no
    /// build ever wrote a preference. It is omitted rather than written as <c>null</c> so a state file
    /// written here still loads in an older build, whose <c>AppLanguage</c> is not nullable.
    /// </para>
    /// <para>
    /// A value this build does not recognise reads back as <c>null</c> rather than throwing - see
    /// <see cref="TolerantAppLanguageConverter"/>. An unreadable preference is a preference we do not
    /// have; it must never cost the user their catalog.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AppLanguage? Language { get; init; }

    /// <summary>
    /// The app colour-theme preference. System means Windows chooses the active palette for this
    /// session; an older state file without this field therefore follows Windows by default.
    /// </summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

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

    /// <summary>
    /// When true, an ordinary launch restarts whatever was playing when the application last closed
    /// (SP-0062). Defaults off: an older state file lacking this key deserializes to the initializer
    /// default.
    /// <para>
    /// Off is deliberately also the answer for an installation upgraded from a build that predates this
    /// key, and that <em>withdraws</em> a shipped behaviour. SP-0008 made an argument-free launch play
    /// <see cref="LastSelectedChannelId"/> unconditionally, and that id is written by merely highlighting
    /// a row - so the application could open and stream a channel the user had never listened to, with no
    /// way to stop it. The owner's decision on 2026-08-07 is that this was the defect, so the behaviour is
    /// withdrawn rather than migrated forward. A reader who finds the launch path with no automatic play
    /// in it is looking at a decision, not an omission.
    /// </para>
    /// </summary>
    public bool ResumePlaybackOnStartup { get; init; }

    /// <summary>
    /// The channels that were playing when the application last closed, in the order they started, or
    /// empty when nothing was (SP-0062). Read at launch only, and only when
    /// <see cref="ResumePlaybackOnStartup"/> is set; it is never merged, never uploaded, and never
    /// consulted by the catalog.
    /// <para>
    /// A <see cref="List{T}"/> rather than a set because two player windows may hold the same channel, and
    /// forgetting one of them when the other closes would lose a window the user had open. Maintained on
    /// the boundaries of each listening session while the application runs - not written at exit - so an
    /// abnormal termination leaves the last truthful value behind rather than nothing.
    /// </para>
    /// </summary>
    public List<Guid> ResumeChannelIds { get; init; } = [];
    public StreamTileSize TileSize { get; init; } = StreamTileSize.Medium;
    public bool UpdateStreamPreviews { get; init; } = true;

    /// <summary>
    /// Which published artwork build seeded the local preview store, as
    /// <c>artwork-manifest.json</c> stamps it (SP-0091), or null when none has.
    /// </summary>
    /// <remarks>
    /// <para>A record, not a gate. It replaces <c>ChannelPreviewAtlasRevision</c>, which held a
    /// compiled-in revision suffix - a value that said which asset name this client was pinned to and
    /// therefore could never disagree with itself. The stamp says which bytes actually landed, which is
    /// the question a support log needs answered: two installs reporting the same seeded count and
    /// different stamps are not the same install.</para>
    /// <para>The old key is not migrated. Its values named frozen sheet revisions that no longer
    /// identify anything, so carrying one forward would assert a build we cannot name; absent reads as
    /// "unknown", which is the truth. Written only after a successful import - a failed or declined one
    /// leaves it as it was. SP-0088 made the offer ask every time, so nothing reads this to decide
    /// whether to ask: a later catalog can bring channels the same artwork build already covers.</para>
    /// </remarks>
    public string? ChannelPreviewArtworkStamp { get; init; }

    /// <summary>
    /// Playback engine for the video/RTSP player window only (SP-0026). Defaults to
    /// <see cref="MediaBackend.LibVlc"/> - the proven baseline; <see cref="MediaBackend.Flyleaf"/>
    /// is an opt-in troubleshooting fallback. Audio and headless thumbnail capture ignore this.
    /// An older state file lacking this key deserializes to the LibVlc default.
    /// </summary>
    public MediaBackend VideoBackend { get; init; } = MediaBackend.LibVlc;

    /// <summary>
    /// Folder the player writes captured frames into (SP-0038), or <c>null</c> when the user never
    /// chose one - which means the Windows Downloads folder, resolved at save time. Deliberately not
    /// filled in on first run: writing a resolved path here would freeze a "Downloads" the user may
    /// later move, and would turn a default into a preference the user never expressed.
    /// </summary>
    public string? FrameFolder { get; init; }

    // ---------------------------------------------------------------------------------------------
    // Migration-only as of SP-0067. The browsing session - search text, facets, sort order, scroll
    // position, last selected channel - lives in browsing-session.json now, because it changes several
    // times a minute and rewriting the channel catalog to record a scroll offset cost 15.15 MB and up
    // to 377 ms per pause on the owner's 19 855 channels.
    //
    // The fields stay here, and stay readable, for exactly one purpose: BrowsingSessionStore.LoadAsync
    // reads them once when it has no file of its own, and never again. Nothing writes them any more.
    // Do not delete them - that breaks the migration and every state file written before the split. Do
    // not mark them [Obsolete] either: the store that legitimately reads them would warn. A downgrade
    // to a build older than SP-0067 loses the saved session but keeps the catalog, which is the
    // accepted trade (settled question 3).
    //
    // CatalogScrollAnchorId has no counterpart in the session: it named a channel, and the session
    // stores a pixel position. It migrates to ScrollOffset = 0.
    // ---------------------------------------------------------------------------------------------

    public Guid? LastSelectedChannelId { get; init; }
    public string CatalogSearchQuery { get; init; } = string.Empty;
    public string CatalogMediaFilter { get; init; } = "All";
    public string CatalogCategoryFilter { get; init; } = "All";
    public string CatalogLanguageFilter { get; init; } = "All";
    public string CatalogCountryFilter { get; init; } = "All";

    /// <summary>
    /// Rubric filter (SP-0061): "All" (default) or a rubric identifier exactly as the bank spells it -
    /// never a translated label, so the value keeps its meaning when the interface language changes.
    /// Migration-only since SP-0067; the live value is <see cref="BrowsingSession.TopicFilter"/>.
    /// </summary>
    public string CatalogTopicFilter { get; init; } = "All";

    /// <summary>
    /// Minimum-bitrate filter (SP-0018). "All" (default) leaves the catalog view unchanged; a numeric
    /// kbps threshold excludes rows whose advertised bitrate is missing or cannot be interpreted.
    /// Migration-only since SP-0067; the live value is <see cref="BrowsingSession.MinBitrateFilter"/>.
    /// </summary>
    public string CatalogMinBitrateFilter { get; init; } = "All";

    /// <summary>
    /// Active collection view (SP-0017): "All" (default) or a collection id. A stale id - the
    /// collection was deleted while the app was closed - falls back to "All" instead of an empty list.
    /// Migration-only since SP-0067; the live value is <see cref="BrowsingSession.CollectionFilter"/>.
    /// </summary>
    public string CatalogCollectionFilter { get; init; } = "All";
    public string CatalogSortMode { get; init; } = "Name";
    public Guid? CatalogScrollAnchorId { get; init; }

    /// <summary>
    /// Whether the filter and sorting row is mounted in the main window (SP-0050). The default is
    /// hidden: a state file written before this change simply lacks the key and deserializes to the
    /// initializer default, so the fallback is structural rather than a special case in the reader.
    /// Deliberately independent of the facet values it reveals - hiding the row never resets a facet,
    /// and a hidden row keeps narrowing the catalog exactly as a visible one does.
    /// </summary>
    public bool CatalogFiltersVisible { get; init; }

    /// <summary>
    /// Collapsed/expanded state of the two catalog sections (SP-0025). Both default to expanded;
    /// an older state file lacking these keys deserializes to the initializer default (false).
    /// </summary>
    public bool PinnedSectionCollapsed { get; init; }
    public bool MainSectionCollapsed { get; init; }
}

/// <summary>
/// How <see cref="CatalogMerger.Merge"/> treats the entries it is given (SP-0052). The defaults are
/// exactly what an explicit online refresh does, so every call site that predates the bundled snapshot
/// keeps its meaning without naming them; only applying the snapshot passes anything else.
/// </summary>
/// <param name="RemoveMissing">
/// Whether catalog rows absent from the entries are pruned. False for the bundled snapshot, which is
/// always at least as old as the last download: pruning against it would roll a freshly updated catalog
/// back to release day.
/// </param>
/// <param name="FaviconSource">
/// The atlas the entries' <c>favicon_index</c> values index, stamped on every row this merge adds or
/// updates so an index and its own atlas always travel together.
/// </param>
public sealed record CatalogMergeOptions(
    bool RemoveMissing = true,
    FaviconSource FaviconSource = FaviconSource.Catalog)
{
    public static readonly CatalogMergeOptions CatalogRefresh = new();
}

/// <param name="Removed">
/// Catalog rows the bank no longer lists that carried nothing the user authored, and were therefore
/// deleted. Always 0 when <see cref="CatalogMergeOptions.RemoveMissing"/> is false.
/// </param>
/// <param name="Retired">
/// Catalog rows the bank no longer lists that were kept anyway, because deleting them would have taken
/// a pin, a collection membership or a history entry with them (SP-0089). This is the count in the
/// retired state after the merge, not the count newly retired by it: the question a log has to answer is
/// how many rows are being kept without being offered, and a row retired two refreshes ago is still one
/// of them. Added and Removed already account for the transitions.
/// </param>
public sealed record MergeResult(
    IReadOnlyList<StreamChannel> Channels,
    int Added,
    int Updated,
    int Removed,
    int Retired);

/// <summary>
/// Outcome of <see cref="CatalogPurge.RemoveDownloaded"/> (SP-0030): the state without downloaded
/// rows, and the ids that left so the UI can release their cached rows, selection, and playback.
/// </summary>
public sealed record CatalogPurgeResult(
    CatalogState State,
    IReadOnlyList<Guid> RemovedChannelIds);

/// <param name="AtlasReplaced">
/// Whether this refresh installed the bank's own icon atlas. <c>false</c> means the archive carried no
/// usable atlas - absent, empty, or past <see cref="StreamBankReader.MaximumAtlasBytes"/>, which the
/// reader drops silently - and the previously installed atlas was therefore kept, because deleting it
/// would strip the icons off every channel over one mispackaged upload. Since SP-0088 that build's
/// favicon indices are discarded instead of pointed at the surviving sheet, so the outcome is missing
/// icons for one refresh rather than confidently wrong ones. Still worth a log line: it is the one
/// refresh outcome with no visible failure, and the icons the user loses came back on their own.
/// </param>
/// <param name="Retired">
/// Catalog rows kept despite the bank no longer listing them, because the user had authored something on
/// them (SP-0089). Not part of <paramref name="Removed"/>: these rows did not leave, they stopped being
/// offered.
/// </param>
public sealed record CatalogRefreshResult(
    CatalogState State,
    int Added,
    int Updated,
    int Removed,
    bool AtlasReplaced = true,
    int Retired = 0);
