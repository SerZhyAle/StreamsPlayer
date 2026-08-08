using System.Windows;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0067: everything that turns <c>_state.Channels</c> into what the list shows. Split out of
/// <c>MainWindow.xaml.cs</c>, which was over the ~500 line budget with this work in it.
/// </summary>
/// <remarks>
/// The rule this file exists to enforce: <b>one interaction costs one evaluation</b>. Before it, typing
/// a six-character query ran the filter six times - once per keystroke - and each run walked the whole
/// 19 855-channel catalog about nine times over, then refilled the bound collection one item at a time.
/// Measured on the owner's catalog: 6 runs, 44 to 161 ms each.
/// </remarks>
public partial class MainWindow
{
    // Typed characters and dropdown changes settle before they are applied. 200 ms is below the
    // threshold where a filter feels detached from the keystroke and above a fast typist's inter-key
    // gap, so a six-character word costs one evaluation instead of six.
    private static readonly TimeSpan FilterDebounceInterval = TimeSpan.FromMilliseconds(200);
    private DispatcherTimer? _filterDebounce;

    // Reused across evaluations so a filter change allocates two lists once rather than per keystroke.
    private readonly List<StreamChannel> _pinnedMatches = [];
    private readonly List<StreamChannel> _unpinnedMatches = [];
    private readonly List<ChannelRow> _pinnedRowBuffer = [];
    private readonly List<ChannelRow> _rowBuffer = [];

    // Both of these are derived from _state, and both used to be recomputed on every keystroke even
    // though a keystroke cannot change what they depend on. The reference check is what makes that
    // true rather than hopeful, and it rests on one invariant worth stating precisely:
    //
    //   every change to the *membership* of the channel list goes through `_state with { Channels = }`
    //   - add (MainWindow.xaml.cs), import (MainWindow.ImportExport.cs), hide/delete
    //   (MainWindow.Hide.cs), purge, and the refresh's result.State - so it always yields a new
    //   CatalogState instance.
    //
    // ReplaceChannel is the one path that mutates in place, and it only ever swaps one element for
    // another; it never adds or removes. That is exactly what these two caches need. PruneRowCache
    // only cares about channels that disappeared, and the atlas bounds are maxima over favicon indices
    // that an element swap does not raise.
    //
    // Keyed on _state rather than on _state.Channels because BuildFaviconAtlasSet also reads the atlas
    // file names, which the snapshot import can replace without touching the channel list.
    private CatalogState? _atlasSetSource;
    private FaviconAtlasSet? _atlasSet;
    private CatalogState? _prunedSource;

    private void FilterChanged(object sender, EventArgs e)
    {
        if (IsLoaded && !_updatingLocalizedOptions && !_restoringBrowsingSession && !_resettingFilters)
        {
            ScheduleFilterEvaluation();
        }
    }

    private void ScheduleFilterEvaluation()
    {
        // Recorded before the debounce, so one run's log proves the collapse on its own: six of these
        // against one op=ApplyFilter is what "a six-character query costs one evaluation" means, and it
        // needs no comparison against a differently-built binary to read.
        _log.Event("CATALOG PERF", "op=FilterRequested");
        _filterDebounce ??= CreateFilterDebounce();
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private DispatcherTimer CreateFilterDebounce()
    {
        var timer = new DispatcherTimer { Interval = FilterDebounceInterval };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplyFilterNow();
        };
        return timer;
    }

    /// <summary>
    /// Applies a pending debounced evaluation immediately, if there is one.
    /// </summary>
    /// <remarks>
    /// Two callers must not wait out the debounce: clearing the filters, which the user expects to act
    /// at once, and closing the window, where a pending timer would otherwise be dropped and the
    /// session saved from the state before the user's last change.
    /// </remarks>
    private void FlushPendingFilterEvaluation()
    {
        if (_filterDebounce?.IsEnabled == true)
        {
            _filterDebounce.Stop();
            ApplyFilterNow();
        }
    }

    private void ApplyFilterNow()
    {
        ApplyFilter();
        UpdateFilterPanelChrome();
        ScrollToCatalogStart();
        ScheduleBrowsingSessionSave();
    }

    // Resets the facet filters only: the search text and the sort order are deliberately kept.
    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        _resettingFilters = true;
        try
        {
            SelectOptionValue(MediaFilter, AllValue, AllValue);
            SelectOptionValue(CategoryFilter, AllValue, AllValue);
            SelectOptionValue(TopicFilter, AllValue, AllValue);
            SelectOptionValue(LanguageFilter, AllValue, AllValue);
            SelectOptionValue(CountryFilter, AllValue, AllValue);
            SelectOptionValue(MinBitrateFilter, AllValue, AllValue);
            SelectOptionValue(CollectionFilter, AllValue, AllValue);
        }
        finally
        {
            _resettingFilters = false;
        }

        // Not debounced: a button press is a completed intention, not a keystroke in the middle of one.
        _filterDebounce?.Stop();
        ApplyFilterNow();
    }

    /// <summary>
    /// Rebuilds <see cref="PinnedRows"/>, <see cref="Rows"/> and <see cref="GridRows"/> from
    /// <c>_state.Channels</c> under the current filters.
    /// </summary>
    /// <remarks>
    /// That contract is unchanged and load-bearing: this is also called by the add path, hide/unhide,
    /// import, purge, pin/unpin, the play-outcome record and the localization refresh, none of which go
    /// through the debounce. What changed is the cost - one pass over the catalog instead of the nine
    /// the chained <c>Where</c>/<c>Count</c> enumerations produced, and one collection notification
    /// instead of one per channel.
    /// </remarks>
    private void ApplyFilter()
    {
        var started = BeginCatalogPerf();
        var query = SearchBox.Text.Trim();
        var category = SelectedOptionValue(CategoryFilter);
        var topic = SelectedOptionValue(TopicFilter);
        var language = SelectedOptionValue(LanguageFilter);
        var country = SelectedOptionValue(CountryFilter);
        var media = SelectedOptionValue(MediaFilter) ?? AllValue;
        var minBitrate = SelectedOptionValue(MinBitrateFilter) ?? AllValue;
        int? minBitrateKbps = minBitrate != AllValue &&
            int.TryParse(minBitrate, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedMinBitrate)
            ? parsedMinBitrate
            : null;
        var hiddenIdentities = BuildHiddenIdentitySet();
        var collectionMembers = ActiveCollectionMembers();

        _pinnedMatches.Clear();
        _unpinnedMatches.Clear();
        var visibleUniverse = 0;

        // The one pass. Everything the old chain computed separately - the two Where enumerations for
        // pinned and unpinned, and the Count over the unhidden universe for the status line - falls out
        // of this loop.
        var channels = _state.Channels;
        for (var index = 0; index < channels.Count; index++)
        {
            var channel = channels[index];
            if (IsHiddenBySet(hiddenIdentities, channel))
            {
                continue;
            }

            visibleUniverse++;
            if (collectionMembers is not null && !collectionMembers.Contains(channel.Id))
            {
                continue;
            }

            // SP-0061: the rubric is matched by its translated label as well as by the stored English
            // identifier - a reader searches with the word printed on the screen. The identifier is kept
            // so a saved query, an English rubric name and a hand-typed one all still find their rows.
            if (query.Length > 0 &&
                !Contains(channel.Title, query) && !Contains(channel.Topic, query) &&
                !Contains(TopicLabels.Text(channel.Topic), query) && !Contains(channel.Language, query))
            {
                continue;
            }

            if (category is not (null or AllValue) &&
                !string.Equals(channel.Category, category, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Compared against the identifier, never the label: the facet's value is data.
            if (topic is not (null or AllValue) &&
                !string.Equals(channel.Topic, topic, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (language is not (null or AllValue) && !LanguageContains(channel.Language, language))
            {
                continue;
            }

            if (country is not (null or AllValue) &&
                !string.Equals(channel.Country, country, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (media != AllValue &&
                !(media == "Audio" && channel.MediaKind == MediaKind.Audio) &&
                !(media == "Video" && channel.MediaKind is MediaKind.Video or MediaKind.Rtsp))
            {
                continue;
            }

            if (minBitrateKbps is int min && !StreamBitrate.MeetsMinimum(channel.Bitrate, min))
            {
                continue;
            }

            // SP-0025: pinned and unpinned are two distinct sections. Both honour the shared sort (the
            // pinned set no longer keeps a separate SortIndex-only order).
            (channel.Pinned ? _pinnedMatches : _unpinnedMatches).Add(channel);
        }

        var atlases = BuildFaviconAtlasSet();
        PruneRowCache();

        _pinnedRowBuffer.Clear();
        foreach (var channel in SortChannels(_pinnedMatches))
        {
            _pinnedRowBuffer.Add(GetOrCreateRow(channel, atlases));
        }

        _rowBuffer.Clear();
        _rowIndex.Clear();
        foreach (var channel in SortChannels(_unpinnedMatches))
        {
            var row = GetOrCreateRow(channel, atlases);
            _rowIndex[row] = _rowBuffer.Count;
            _rowBuffer.Add(row);
        }

        PinnedRows.ReplaceAll(_pinnedRowBuffer);
        Rows.ReplaceAll(_rowBuffer);
        RebuildGridRows();

        var totalShown = PinnedRows.Count + Rows.Count;
        EmptyPanel.Visibility = totalShown == 0 ? Visibility.Visible : Visibility.Collapsed;
        StreamsList.Visibility = Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NotifySectionState();
        UpdatePinnedSectionLayout();
        if (!_busy)
        {
            SetStatus("ChannelCount", totalShown, visibleUniverse);
        }
        ScheduleVisiblePreviewUpdate();
        // Closed after the nested RebuildGridRows record, so the filter's own line is the outer of the
        // two and its ms includes the re-chunk it drives.
        CatalogPerf("ApplyFilter", started, $"catalog={channels.Count}", $"shown={totalShown}");
    }

    private void UpdateCatalogColumns()
    {
        var minimumCardWidth = IsGridMode ? GridTileWidth + 6 : 330;
        var availableWidth = Math.Max(0, StreamsList.ActualWidth - 12);
        var columns = Math.Max(1, (int)Math.Floor(availableWidth / minimumCardWidth));
        if (IsGridMode && availableWidth >= minimumCardWidth * 2)
        {
            columns = Math.Max(2, columns);
        }
        if (columns != _catalogColumns)
        {
            _catalogColumns = columns;
            RebuildGridRows();
        }
    }

    /// <summary>
    /// Re-chunks <see cref="Rows"/> into <see cref="GridRows"/> for the current column count, reusing
    /// the row objects already there.
    /// </summary>
    /// <remarks>
    /// A column-count change moves every chunk boundary, so this is proportional to the number of
    /// channels shown and always will be - the strategic ticket rejected the virtualizing wrapping panel
    /// that would make it constant-time, and recorded why. What it stops being is <em>allocating</em>:
    /// the <see cref="CatalogGridRow"/> instances are re-pointed rather than rebuilt, so a drag across
    /// five column boundaries reuses 2481 to 6616 objects instead of discarding and re-creating them.
    /// The small slice array is still fresh each time, deliberately - handing back the same array
    /// instance would leave the bound <c>ItemsSource</c> unable to tell that its contents changed.
    /// </remarks>
    private void RebuildGridRows()
    {
        var started = BeginCatalogPerf();
        var columns = _catalogColumns;
        var chunkCount = columns <= 0 ? 0 : (Rows.Count + columns - 1) / columns;
        var reusable = Math.Min(chunkCount, GridRows.Count);

        _gridRowBuffer.Clear();
        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            var first = chunk * columns;
            var length = Math.Min(columns, Rows.Count - first);
            var items = new ChannelRow[length];
            for (var offset = 0; offset < length; offset++)
            {
                items[offset] = Rows[first + offset];
            }

            if (chunk < reusable)
            {
                var existing = GridRows[chunk];
                existing.Update(items, columns);
                _gridRowBuffer.Add(existing);
            }
            else
            {
                _gridRowBuffer.Add(new CatalogGridRow(items, columns));
            }
        }

        // When the row count did not change, every row was updated in place and the collection still
        // holds exactly the same instances in the same order - the bindings have already followed the
        // per-row notifications, and a Reset here would throw away selection and scroll position for
        // nothing. Only a changed length needs the collection itself to be re-stated.
        if (chunkCount != GridRows.Count)
        {
            GridRows.ReplaceAll(_gridRowBuffer);
        }

        RebuildPinnedGridRows(columns);
        CatalogPerf("RebuildGridRows", started,
            $"columns={columns}", $"rows={GridRows.Count}", $"reused={reusable}");
    }

    // SP-0067: the pinned tile list virtualizes, so it needs the same uniform chunked rows the main
    // list has, on the same column count - which is what keeps pinned and unpinned tiles on one grid.
    // Small by comparison (the pinned set is a subset), so this is rebuilt rather than reused.
    private void RebuildPinnedGridRows(int columns)
    {
        var chunkCount = columns <= 0 ? 0 : (PinnedRows.Count + columns - 1) / columns;
        _pinnedGridRowBuffer.Clear();
        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            var first = chunk * columns;
            var length = Math.Min(columns, PinnedRows.Count - first);
            var items = new ChannelRow[length];
            for (var offset = 0; offset < length; offset++)
            {
                items[offset] = PinnedRows[first + offset];
            }

            _pinnedGridRowBuffer.Add(new CatalogGridRow(items, columns));
        }

        PinnedGridRows.ReplaceAll(_pinnedGridRowBuffer);
    }

    private readonly List<CatalogGridRow> _pinnedGridRowBuffer = [];

    private readonly List<CatalogGridRow> _gridRowBuffer = [];

    // A window drag raises SizeChanged on every pixel. 120 ms is short enough that the layout lands
    // while the mouse is still down on a slow drag, and long enough that a fast one across five column
    // boundaries settles into a single re-chunk instead of five.
    private static readonly TimeSpan ResizeDebounceInterval = TimeSpan.FromMilliseconds(120);
    private DispatcherTimer? _resizeDebounce;

    private void ScheduleColumnUpdate()
    {
        if (_resizeDebounce is null)
        {
            _resizeDebounce = new DispatcherTimer { Interval = ResizeDebounceInterval };
            _resizeDebounce.Tick += (_, _) =>
            {
                _resizeDebounce.Stop();
                UpdateCatalogColumns();
            };
        }

        _resizeDebounce.Stop();
        _resizeDebounce.Start();
    }

    /// <summary>
    /// Drops rows whose channel is gone for good, so the images hanging off them are not held for the
    /// rest of the session.
    /// </summary>
    /// <remarks>
    /// Rows are cached across filter changes on purpose - they hold the decoded favicon and preview
    /// images - which is why this prunes by "the channel no longer exists" and not by "not currently
    /// shown". The row a launch URL created is kept: an externally launched channel is deliberately
    /// absent from the catalog. Nothing here can change while <c>_state</c> does not, so a keystroke no
    /// longer pays for it.
    /// </remarks>
    private void PruneRowCache()
    {
        if (ReferenceEquals(_prunedSource, _state))
        {
            return;
        }

        _prunedSource = _state;
        var live = _state.Channels.Select(channel => channel.Id).ToHashSet();
        var stale = _rowCache
            .Where(entry => !live.Contains(entry.Key) &&
                !ReferenceEquals(entry.Value, _playingAudio) &&
                !ReferenceEquals(entry.Value, _selectedRow))
            .Select(entry => entry.Key)
            .ToList();
        foreach (var id in stale)
        {
            if (_rowCache.Remove(id, out var row))
            {
                UnindexUrl(row.Channel.Url, row);
            }
        }
    }

    // SP-0052: each source's bound is taken over its own rows, because a favicon index is an offset into
    // one specific sheet - a bound borrowed from the other atlas would either clip valid icons or let an
    // out-of-range index through to a crop that lands on a stranger's tile.
    private FaviconAtlasSet BuildFaviconAtlasSet()
    {
        if (_atlasSet is { } cached && ReferenceEquals(_atlasSetSource, _state))
        {
            return cached;
        }

        int? MaximumIndexOf(FaviconSource source) => _state.Channels
            .Where(channel => channel.SourceOrigin == SourceOrigin.Catalog && channel.FaviconSource == source)
            .Select(channel => channel.FaviconIndex)
            .DefaultIfEmpty(null)
            .Max();

        var built = new FaviconAtlasSet(
            _store.ResolveAtlasPath(_state),
            MaximumIndexOf(FaviconSource.Catalog),
            _store.ResolveAtlasPath(_state, AtlasSlot.Snapshot),
            MaximumIndexOf(FaviconSource.Snapshot));
        _atlasSetSource = _state;
        _atlasSet = built;
        return built;
    }

    private ChannelRow GetOrCreateRow(StreamChannel channel, FaviconAtlasSet atlases)
    {
        if (_rowCache.TryGetValue(channel.Id, out var cached))
        {
            cached.UpdatePresentation(atlases);
            // The edit dialog can change a channel's URL while its identity stays the same, so the URL
            // index has to be re-keyed before the row starts reporting the new one - otherwise a preview
            // for either URL would find nothing.
            var previousUrl = cached.Channel.Url;
            cached.UpdateChannel(channel);
            if (!previousUrl.Equals(channel.Url, StringComparison.Ordinal))
            {
                UnindexUrl(previousUrl, cached);
                IndexUrl(cached);
            }

            return cached;
        }

        var row = new ChannelRow(channel, atlases);
        _rowCache[channel.Id] = row;
        IndexUrl(row);
        return row;
    }

    private void IndexUrl(ChannelRow row)
    {
        if (!_rowsByUrl.TryGetValue(row.Channel.Url, out var rows))
        {
            _rowsByUrl[row.Channel.Url] = rows = [];
        }

        if (!rows.Contains(row))
        {
            rows.Add(row);
        }
    }

    private void UnindexUrl(string url, ChannelRow row)
    {
        if (_rowsByUrl.TryGetValue(url, out var rows) && rows.Remove(row) && rows.Count == 0)
        {
            _rowsByUrl.Remove(url);
        }
    }

    /// <remarks>
    /// Left as <c>OrderBy</c> chains on purpose: this sorts what is <em>shown</em>, which is the rule
    /// this ticket enforces rather than a violation of it, and <c>OrderBy</c> is a stable sort where
    /// <c>List.Sort</c> is not - reordering ties would be a visible change nobody asked for.
    /// </remarks>
    private IEnumerable<StreamChannel> SortChannels(IEnumerable<StreamChannel> channels) =>
        SelectedOptionValue(SortMode) switch
        {
            // SP-0061: ordered by the rubric the reader can see, under the interface culture. An ordinal
            // sort over the English identifier put the Russian and Arabic lists in what looked like
            // random order, because the string being compared was not the string on screen.
            "Topic" => channels.OrderBy(channel => channel.Topic is null).ThenBy(channel => channel.Topic ?? string.Empty, TopicLabels.Comparer),
            "Language" => channels.OrderBy(channel => channel.Language is null).ThenBy(channel => channel.Language, StringComparer.OrdinalIgnoreCase),
            "Country" => channels.OrderBy(channel => channel.Country is null).ThenBy(channel => channel.Country, StringComparer.OrdinalIgnoreCase),
            "Recently added" => channels.OrderByDescending(channel => channel.AddedAt),
            _ => channels.OrderBy(channel => channel.Title, StringComparer.OrdinalIgnoreCase)
        };

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static bool LanguageContains(string? value, string language) =>
        value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(item => item.Equals(language, StringComparison.OrdinalIgnoreCase)) == true;
}
