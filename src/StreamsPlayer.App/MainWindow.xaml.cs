using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string AllValue = "All";
    private readonly string _dataDirectory = AppPaths.DataDirectory;
    private readonly HttpClient _httpClient;

    // SP-0056: dedicated, because HttpClient.Timeout severs the body read even under
    // ResponseHeadersRead - so a shared 30 s timeout would cut the 7.2 MB ZIP on any link slower than
    // 250 KB/s and the service's idle bound would never get to act. Raising the shared client's timeout
    // instead would silently unbound the playlist import and the components installer, which is not this
    // change's business. Same reason the preview atlas already has its own client.
    private readonly HttpClient _catalogHttpClient = CreateCatalogHttpClient();

    private static HttpClient CreateCatalogHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamsPlayer/0.1");
        return client;
    }
    private readonly CurrentLog _log;
    private readonly StreamCatalogStore _store;
    // SP-0067: the browsing session has its own small file. It changes several times a minute and the
    // catalog does not, so they no longer share a serialization.
    private readonly BrowsingSessionStore _sessionStore;
    private readonly Dictionary<Guid, ChannelRow> _rowCache = [];
    // SP-0067: the same rows, indexed the way the preview pipeline asks for them. Maintained beside
    // _rowCache in GetOrCreateRow and PruneRowCache, which are the only two places that add or drop a
    // row - see MainWindow.CatalogView.cs.
    private readonly Dictionary<string, List<ChannelRow>> _rowsByUrl = new(StringComparer.Ordinal);
    // SP-0067: position of each row in Rows, for arrow-key navigation. Rows.IndexOf was a linear scan
    // per keypress over as many entries as the filter left showing.
    private readonly Dictionary<ChannelRow, int> _rowIndex = [];
    private readonly HashSet<PlayerWindow> _playerWindows = [];
    private readonly GridPreviewCoordinator? _previewCoordinator;
    // Kept beside the coordinator so the SP-0031 atlas import can seed the same store the grid reads.
    private readonly PreviewFrameStore? _previewFrameStore;

    // Decoded previews held in memory. Every eviction blanks that tile back to its favicon until the row
    // scrolls back into view, so this must comfortably exceed one viewport's worth of tiles (pinned band
    // included) or ordinary scrolling visibly strips the grid.
    // SP-0069: the bound is entries, not bytes, and an entry's cost depends on where the frame came from -
    // a 240x135 atlas tile is ~130 KB, but a 480x270 live capture (VideoFrameCaptureService) is 518 400 B.
    // So this cap is 24 MiB of imported previews or 95 MiB of captured ones, and the accepted ceiling is
    // the larger figure. A byte budget was considered and rejected: these are frozen BitmapSources whose
    // pixels live in unmanaged WIC memory the cache does not own, so it would have to estimate the very
    // number it claimed to enforce. The comment used to quote only the small figure, which read as a 24 MiB
    // ceiling that was never true.
    private const int PreviewMemoryCacheCapacity = 192;
    private int _previewEvictions;
    private readonly StreamLaunchRequest _launchRequest;
    private CatalogState _state = new();
    private BrowsingSession _session = new();
    private ChannelRow? _playingAudio;
    private LivePlaybackRecoveryPolicy? _audioRecovery;
    private CancellationTokenSource? _audioRecoveryCts;
    private IDisposable? _audioWake;
    private bool _suppressAudioVolumeSave;
    private ChannelRow? _selectedRow;
    private bool _busy;
    // SP-0059: whether this launch found no state file at all. Read once, before the load.
    private bool _cleanInstall;
    private bool _isGridMode;
    private bool _windowActive = true;
    private int _openPlayerWindows;
    private int _catalogColumns = 1;
    private CancellationTokenSource? _viewportDebounce;
    private CancellationTokenSource? _hoverDwell;
    // SP-0065: this window is closing, so the preview subsystem is closed for business. Set as the very
    // first statement of MainWindow_Closed - the handler is async void and the dispatcher pumps input
    // while it saves, so events keep arriving right through the teardown that disposes the two sources
    // above. Every preview entry point reads it; see MainWindow.Previews.cs.
    private bool _shuttingDown;
    private readonly DispatcherTimer _browsingSessionSaveTimer;
    private bool _restoringBrowsingSession;
    private bool _resettingFilters;
    // SP-0067: a pixel offset read off the scroll event, not a channel identity searched for among the
    // rows. See RestoreScrollAnchorAsync for why approximate is the accepted answer here.
    private double _lastScrollOffset;
    private ScrollViewer? _streamsScroll;

    internal MainWindow(CurrentLog log, StreamLaunchRequest? launchRequest = null)
    {
        InitializeComponent();
        _log = log;
        _launchRequest = launchRequest ?? new StreamLaunchRequest(StreamLaunchTargetKind.None);
        DataContext = this;
        _store = new StreamCatalogStore(_dataDirectory);
        _sessionStore = new BrowsingSessionStore(_dataDirectory);
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("StreamsPlayer/0.1");
        _browsingSessionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _browsingSessionSaveTimer.Tick += BrowsingSessionSaveTimer_Tick;
        if (GridPreviewFeature.CaptureEnabled)
        {
            var memoryCache = new PreviewFrameCache(PreviewMemoryCacheCapacity, url =>
            {
                _previewEvictions++;
                if (Dispatcher.CheckAccess())
                {
                    ClearPreview(url);
                }
                else
                {
                    Dispatcher.Invoke(() => ClearPreview(url));
                }
            });
            const long previewDiskBudgetBytes = 150L * 1024 * 1024;
            var frameStore = new PreviewFrameStore(Path.Combine(_dataDirectory, "grid-previews"), previewDiskBudgetBytes, 70);
            _previewFrameStore = frameStore;
            var captureService = new VideoFrameCaptureService();
            _previewCoordinator = new GridPreviewCoordinator(
                Dispatcher,
                GetVisibleRows,
                ApplyPreview,
                memoryCache,
                frameStore,
                captureService,
                url => _log.Event("PREVIEW FAIL", $"url={url}"),
                () => _state.UpdateStreamPreviews,
                (category, fields) => _log.Event(category, [.. fields, $"evictions={_previewEvictions}"]));
        }

        UpdateLocalizedOptions();
        Loaded += MainWindow_Loaded;
        Activated += MainWindow_Activated;
        Deactivated += MainWindow_Deactivated;
        Closing += MainWindow_Closing; // SP-0062: freeze the resume record before shutdown tears playback down
        Closed += MainWindow_Closed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public CatalogRowCollection<ChannelRow> Rows { get; } = [];
    public CatalogRowCollection<CatalogGridRow> GridRows { get; } = [];
    public bool IsGridMode
    {
        get => _isGridMode;
        private set
        {
            if (_isGridMode == value)
            {
                return;
            }

            _isGridMode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGridMode)));
            // SP-0067: the pinned section is two lists picked by view mode now, so its visibility
            // derives from this property. Raised here rather than at the call sites - SetViewModeAsync,
            // the settings apply path and the initial load all assign IsGridMode, and only one of them
            // went on to call NotifySectionState. The first version of this notified nowhere, and list
            // mode kept showing the tile list.
            NotifyPinnedSectionVisibility();
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SetStatus("MainOpening");
        SetBusy(true);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        // SP-0059: asked before the load, because this same launch persists the detected interface
        // language a few lines below - after that write the machine looks used, and the one first-launch
        // question would never be asked again.
        _cleanInstall = !_store.HasStoredState;
        try
        {
            _state = await _store.LoadAsync();
            // SP-0067: right after the catalog, and given it as the migration source. When the session
            // file is absent this is the one read of the old CatalogState fields, ever - it writes the
            // new file in the same call, so the next launch never looks at them again.
            _session = await _sessionStore.LoadAsync(_state);
            ThemeService.Apply(_state.Theme);

            // SP-0034 decision 5: no saved preference means a fresh install, so follow the operating
            // system and fall back to English. A preference that is present is always honoured.
            var savedLanguage = _state.Language;
            var language = savedLanguage ?? InterfaceLanguages.Detect(
                CultureInfo.CurrentUICulture,
                CultureInfo.InstalledUICulture);
            LocalizationService.Apply(language);
            WakeGuard.Enabled = _state.KeepAwakeDuringPlayback;
            Topmost = _state.MainWindowTopmost;
            _preferencesLoaded = true;
            if (savedLanguage is null)
            {
                // Record the detected language once, so the next launch is an ordinary
                // saved-preference launch and a later OS change cannot silently move the interface.
                _state = await PersistAsync(_state with { Language = language });
            }

            UpdateLocalizedOptions();
            IsGridMode = _state.ViewMode == CatalogViewMode.Grid;
            UpdateViewModeControls();
            InitializeSectionState(_state);
            PopulateFacets();
            PopulateCollectionFilter();
            await PruneCollectionsAsync();
            RestoreBrowsingSession();
            // After the restore, so the active-facet count is taken against the facets the user actually
            // left selected rather than against an empty row.
            UpdateFilterPanelChrome();
            ApplyFilter();
            UpdateCatalogColumns();
            await RestoreScrollAnchorAsync();
            _log.Information($"Catalog state loaded: {_state.Channels.Count} channel(s).");
            SetCatalogStatus();
        }
        catch (Exception exception)
        {
            _log.Error("Catalog state load failed", exception);
            SetStatus("MainLoadFailed");
            MessageBox.Show(this, exception.Message, LocalizationService.Get("ProductName"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }

        if (IsGridMode)
        {
            await StartPreviewsAsync();
        }

        await StartRequestedPlaybackAsync();

        if (_cleanInstall)
        {
            // SP-0059: a machine that has never run the product is asked where its channels come from.
            await AskWhereChannelsComeFromAsync();
        }
        else
        {
            // SP-0052: the one first-launch offer, shown after the window has settled so it never
            // competes with the load itself. Nothing is applied until the user accepts it. Kept exactly
            // as it was for every installation that already has local state.
            MaybeOfferCatalogSnapshot();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshCatalogAsync();

    /// <summary>
    /// The catalog import. SP-0059 gave it a name of its own so the first-launch dialog can await the
    /// same path the button and the menu entry use - including its offline refusal, its cancellation
    /// branch, and the bundled-copy offer that follows a failure.
    /// </summary>
    private async Task RefreshCatalogAsync()
    {
        if (_busy)
        {
            return;
        }

        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            _log.Event("REFUSE", "op=catalog_refresh", "reason=offline");
            // SP-0052: the refusal still stands - nothing is downloaded - but the user is offered the
            // bundled snapshot as a way forward instead of a dead end.
            await OfferSnapshotAfterFailedRefreshAsync(LocalizationService.Get("OfflineCatalog"));
            return;
        }

        string? failure = null;
        _cancellableOperation = new CancellationTokenSource();
        _reportingProgress = true;
        var progress = OnDispatcher<DownloadProgress>(report => ShowDownloadProgress(
            report, "CatalogDownloadProgress", "CatalogDownloadProgressUnknown", "CatalogApplying"));
        SetStatus("DownloadingCatalog");
        SetBusy(true, cancellable: true);
        _log.Information("Catalog refresh started.");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        try
        {
            var service = new StreamCatalogService(_catalogHttpClient, _store);
            var result = await service.RefreshAsync(_state, progress, _cancellableOperation.Token);
            // The download is over and the outcome is about to be written, so no further report may touch
            // the status line.
            _reportingProgress = false;
            _state = result.State;
            _log.Information($"Catalog refresh completed: {result.Added} added, {result.Updated} updated, {result.Removed} removed.");
            // Memberships survive a refresh (ids are stable for surviving URLs); only pruned rows are dropped.
            await PruneCollectionsAsync();
            PopulateFacets();
            ApplyFilter();
            SetStatus("CatalogResult", result.Added, result.Updated, result.Removed);
            if (IsGridMode && _previewCoordinator is not null)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                await QueueVisibleSafelyAsync(force: true);
            }

            // SP-0031: the only path that can lead to a preview-atlas download, and only via the user's
            // explicit acceptance of the offer this shows.
            MaybeOfferChannelPreviews();
        }
        // SP-0056: abandoning is not failing. The guard matters: a silent transfer surfaces as
        // TimeoutException precisely so it lands in the general catch below, and a disposed source
        // elsewhere could still raise a cancellation the user never asked for. `failure` stays null, so
        // no snapshot offer follows - offering a fallback is the wrong answer to a deliberate choice.
        catch (OperationCanceledException) when (_cancellableOperation?.IsCancellationRequested == true)
        {
            _log.Event("CANCEL", "op=catalog_refresh");
            SetStatus("CatalogUpdateCancelled");
        }
        catch (Exception exception)
        {
            _log.Error("Catalog refresh failed", exception);
            SetStatus("CatalogUpdateFailedStatus");
            failure = exception.Message;
        }
        finally
        {
            _reportingProgress = false;
            SetBusy(false);
            _cancellableOperation?.Dispose();
            _cancellableOperation = null;
        }

        // SP-0052: outside the busy block, because accepting the offer runs its own busy cycle. The
        // stored catalog is untouched at this point whichever way the user answers.
        if (failure is not null)
        {
            await OfferSnapshotAfterFailedRefreshAsync(failure);
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddStreamWindow { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var url = dialog.StreamUrl.Trim();
        if (_state.Channels.Any(channel => channel.Url.Equals(url, StringComparison.Ordinal)))
        {
            MessageBox.Show(this, LocalizationService.Get("DuplicateStream"), LocalizationService.Get("ProductName"));
            return;
        }

        var title = string.IsNullOrWhiteSpace(dialog.StreamTitle) ? new Uri(url).Host : dialog.StreamTitle.Trim();
        var nextOrder = _state.Channels.Count == 0 ? 0 : _state.Channels.Max(channel => channel.SortIndex) + 1;
        var channel = ApplyDialogMetadata(new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = url,
            Title = title,
            MediaKind = MediaKind.Audio,
            SourceOrigin = SourceOrigin.Manual,
            SortIndex = nextOrder,
            AddedAt = DateTimeOffset.UtcNow
        }, dialog, url, title);
        _state = await PersistAsync(_state with { Channels = [.. _state.Channels, channel] });
        PopulateFacets();
        ApplyFilter();
        SetStatus("AddedStream", title);
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    // SP-0067: debounced. A drag raises this per pixel, and each column-count crossing re-chunks every
    // shown channel. SetViewModeAsync deliberately calls UpdateCatalogColumns directly instead - a view
    // switch must not show the old layout for a beat.
    private void StreamsList_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleColumnUpdate();

    private void StreamsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0)
        {
            // SP-0067: the position arrives on the event args. Nothing is queried and nothing is walked -
            // this replaced a loop over every grid row that asked the generator for a container each time,
            // and which measured scanned=4961 at the end of the owner's catalog against scanned=1 at the top.
            _lastScrollOffset = e.VerticalOffset;
            ScheduleBrowsingSessionSave();
        }

        if (IsGridMode && e.VerticalChange != 0)
        {
            ScheduleVisiblePreviewUpdate();
        }
    }

    // SP-0067: the pinned tile list scrolls independently now that it virtualizes, so scrolling it
    // changes which pinned tiles are on screen and therefore which ones want a preview. Its position is
    // deliberately not part of the browsing session - the section is a handful of rows, and restoring
    // the main list is what the user notices.
    private void PinnedList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (IsGridMode && e.VerticalChange != 0)
        {
            ScheduleVisiblePreviewUpdate();
        }
    }

    private void PopulateFacets()
    {
        var hiddenIdentities = BuildHiddenIdentitySet();
        IReadOnlyList<StreamChannel> universe = hiddenIdentities.Count == 0
            ? _state.Channels
            : _state.Channels.Where(channel => !IsHiddenBySet(hiddenIdentities, channel)).ToList();
        SetFacet(CategoryFilter, universe.Select(channel => channel.Category));
        // SP-0061: built from the channels actually present, not from the registry, so a rubric with no
        // rows is not offered and a rubric this build has never heard of still is. Labels are translated;
        // the option's value stays the catalog's identifier.
        SetFacet(TopicFilter, universe.Select(channel => channel.Topic),
            label: TopicLabels.Text, order: TopicLabels.Comparer);
        // The catalog ships well over a hundred broadcast languages, so the one the user reads the
        // interface in leads the list (with its regional flavours) instead of being hunted for in an
        // alphabetical run. The selected value is deliberately untouched: this orders, it does not filter.
        SetFacet(LanguageFilter, universe.SelectMany(channel =>
            channel.Language?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? []),
            LocalizationService.CurrentLanguage);
        SetFacet(CountryFilter, universe.Select(channel => channel.Country));
    }

    /// <summary>
    /// Fills a facet from the values present in the catalog. <paramref name="label"/> supplies a
    /// translated caption while the option keeps the catalog's own string as its value;
    /// <paramref name="order"/> replaces the default label ordering, and compares identifiers so a
    /// caller can order by something the alphabet alone does not express (SP-0061: General last).
    /// </summary>
    private static void SetFacet(
        ComboBox comboBox,
        IEnumerable<string?> values,
        AppLanguage? preferred = null,
        Func<string, string>? label = null,
        IComparer<string>? order = null)
    {
        var selected = SelectedOptionValue(comboBox) ?? AllValue;
        var options = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new UiOption(value!, label is null ? value! : label(value!)))
            .DistinctBy(value => value.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => preferred is { } language ? (int)CatalogLanguages.Match(value.Value, language) : 0);
        var items = new[] { new UiOption(AllValue, LocalizationService.Get("AllOption")) }.Concat(
            order is null
                ? options.ThenBy(value => value.Value == AllValue ? string.Empty : value.Label, StringComparer.OrdinalIgnoreCase)
                : options.ThenBy(value => value.Value, order)).ToList();
        comboBox.ItemsSource = items;
        comboBox.SelectedItem = items.FirstOrDefault(item => item.Value.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? items[0];
    }

    private async void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChannelRow row)
        {
            return;
        }

        await SetChannelPinnedAsync(row.Channel, !row.Channel.Pinned);
    }

    // Shared pin/unpin path for the catalog row buttons and the video player's pin button.
    // Pinning moves the channel above every other pinned row (min SortIndex - 1); unpinning keeps its order.
    private async Task SetChannelPinnedAsync(StreamChannel channel, bool pinned)
    {
        var current = _state.Channels.FirstOrDefault(item => item.Id == channel.Id) ?? channel;
        if (current.Pinned == pinned)
        {
            return;
        }

        var updated = current with
        {
            Pinned = pinned,
            SortIndex = pinned
                ? _state.Channels.Where(item => item.Pinned).Select(item => item.SortIndex).DefaultIfEmpty(0).Min() - 1
                : current.SortIndex
        };
        ReplaceChannel(updated);
        _state = await PersistAsync(_state);
        ApplyFilter();
    }

    private void OverflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChannelRow row } button)
        {
            return;
        }

        var openItem = new MenuItem
        {
            Header = LocalizationService.Get("MenuOpen"),
            Tag = row
        };
        openItem.Click += OpenMenuItem_Click;
        var fullscreenItem = new MenuItem
        {
            Header = LocalizationService.Get("MenuOpenFullscreen"),
            Tag = row,
            IsEnabled = row.Channel.MediaKind != MediaKind.Audio,
            ToolTip = LocalizationService.Get("MenuFullscreenUnavailable")
        };
        fullscreenItem.Click += OpenFullscreenMenuItem_Click;
        var newWindowItem = new MenuItem
        {
            Header = LocalizationService.Get("MenuOpenNewWindow"),
            Tag = row,
            IsEnabled = row.Channel.MediaKind != MediaKind.Audio,
            ToolTip = LocalizationService.Get("MenuNewWindowUnavailable")
        };
        newWindowItem.Click += OpenNewWindowMenuItem_Click;
        var shortcutItem = new MenuItem
        {
            Header = LocalizationService.Get("CreateDesktopShortcut"),
            Tag = row
        };
        shortcutItem.Click += CreateDesktopShortcutMenuItem_Click;
        // SP-0058: beside the shortcut item because both hand this channel to something outside the window.
        var shareItem = new MenuItem
        {
            Header = LocalizationService.Get("MenuCopyShareText"),
            Tag = row
        };
        shareItem.Click += CopyShareTextMenuItem_Click;
        var editItem = new MenuItem
        {
            Header = LocalizationService.Get("MenuEdit"),
            Tag = row
        };
        editItem.Click += EditMenuItem_Click;
        var aboutItem = new MenuItem
        {
            Header = LocalizationService.Get("MenuAboutChannel"),
            Tag = row
        };
        aboutItem.Click += AboutChannelMenuItem_Click;
        var pinItem = new MenuItem
        {
            Header = LocalizationService.Get(row.Channel.Pinned ? "MenuUnpin" : "MenuPin"),
            Tag = row
        };
        pinItem.Click += PinButton_Click;
        var menu = new ContextMenu { PlacementTarget = button };
        menu.Items.Add(openItem);
        menu.Items.Add(fullscreenItem);
        menu.Items.Add(newWindowItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(shortcutItem);
        menu.Items.Add(shareItem);
        menu.Items.Add(editItem);
        menu.Items.Add(aboutItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(pinItem);
        menu.Items.Add(BuildCollectionMenuItem(row));
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is ChannelRow row)
        {
            Play(row);
        }
    }

    private async void OpenFullscreenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is ChannelRow row && row.Channel.MediaKind != MediaKind.Audio)
        {
            await PlayChannelAsync(row.Channel, rememberSelection: true, startFullscreen: true);
        }
    }

    private void OpenNewWindowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is ChannelRow row && row.Channel.MediaKind != MediaKind.Audio)
        {
            OpenIndependentPlayerWindow(row.Channel);
        }
    }

    private void CreateDesktopShortcutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not ChannelRow row)
        {
            return;
        }

        try
        {
            var path = StreamShortcutService.CreateDesktopShortcut(row.Channel);
            SetStatus("DesktopShortcutCreated", path);
        }
        // The shell writes the file through COM, so a desktop that rejects the path - too long, read-only,
        // a name already held by a directory - arrives as an IOException rather than a COMException.
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            SetStatus("DesktopShortcutFailed");
        }
    }

    // SP-0053: a channel already on screen is described by the engine playing it, so the window is free;
    // for any other channel it opens the stream once, itself.
    private void AboutChannelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not ChannelRow row)
        {
            return;
        }

        var playing = _playerWindows.FirstOrDefault(window => window.Channel.Id == row.Channel.Id);
        var collections = _state.Collections
            .Where(collection => collection.ChannelIds.Contains(row.Channel.Id))
            .Select(collection => collection.Name)
            .ToArray();
        new ChannelInfoWindow(row.Channel, collections, playing is null ? null : playing.DescribeTransmission)
        {
            Owner = this
        }.ShowDialog();
    }

    private async void EditMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not ChannelRow row)
        {
            return;
        }

        var dialog = new AddStreamWindow(row.Channel) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var url = dialog.StreamUrl.Trim();
        if (_state.Channels.Any(channel => channel.Id != row.Channel.Id && channel.Url.Equals(url, StringComparison.Ordinal)))
        {
            MessageBox.Show(this, LocalizationService.Get("DuplicateStream"), LocalizationService.Get("ProductName"));
            return;
        }

        var title = string.IsNullOrWhiteSpace(dialog.StreamTitle) ? new Uri(url).Host : dialog.StreamTitle.Trim();
        var originalOrigin = row.Channel.SourceOrigin;
        var originalUrl = row.Channel.Url;

        // Editing takes ownership: a catalog row becomes Manual so the change survives an explicit refresh
        // (CatalogMerger only touches Catalog rows). Manual/Imported rows are already refresh-safe.
        ReplaceChannel(ApplyDialogMetadata(
            row.Channel with
            {
                SourceOrigin = originalOrigin == SourceOrigin.Catalog ? SourceOrigin.Manual : originalOrigin
            },
            dialog, url, title));

        // If a catalog row's URL changed, hide the original so a refresh does not re-add it as a duplicate.
        if (originalOrigin == SourceOrigin.Catalog &&
            !originalUrl.Equals(url, StringComparison.Ordinal) &&
            !CatalogUrlIdentity.IsHidden(_state.HiddenCatalogUrls, originalUrl))
        {
            _state = _state with { HiddenCatalogUrls = [.. _state.HiddenCatalogUrls, originalUrl] };
        }

        _state = await PersistAsync(_state);
        PopulateFacets();
        ApplyFilter();
        SetStatus("EditedStream", title);
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ChannelRow row)
        {
            Play(row);
        }
    }

    private void StreamsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindChannelRow(e.OriginalSource as DependencyObject) is { } row)
        {
            Play(row);
        }
    }

    private void StreamCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChannelRow row })
        {
            SelectRow(row);
        }
    }

    private void SelectRow(ChannelRow row)
    {
        if (ReferenceEquals(_selectedRow, row))
        {
            return;
        }

        _selectedRow?.SetSelected(false);
        _selectedRow = row;
        _selectedRow.SetSelected(true);
        _ = RememberSelectedChannelAsync(row.Channel.Id);
    }

    private void StreamsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsGridMode || Rows.Count == 0 || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
        {
            return;
        }

        // SP-0067: a dictionary lookup, not a scan of everything the filter left showing.
        var currentIndex = _selectedRow is not null && _rowIndex.TryGetValue(_selectedRow, out var found)
            ? found
            : -1;
        if (currentIndex < 0)
        {
            SelectRow(Rows[0]);
            StreamsList.ScrollIntoView(GridRows[0]);
            e.Handled = true;
            return;
        }

        var nextIndex = e.Key switch
        {
            Key.Left => currentIndex - 1,
            Key.Right => currentIndex + 1,
            Key.Up => currentIndex - _catalogColumns,
            Key.Down => currentIndex + _catalogColumns,
            _ => currentIndex
        };
        if (nextIndex < 0 || nextIndex >= Rows.Count)
        {
            return;
        }

        SelectRow(Rows[nextIndex]);
        StreamsList.ScrollIntoView(GridRows[nextIndex / _catalogColumns]);
        e.Handled = true;
    }

    private static ChannelRow? FindChannelRow(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: ChannelRow row })
            {
                return row;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private async void Play(ChannelRow row)
    {
        await PlayChannelAsync(row.Channel, rememberSelection: true);
    }

    // quiet is set only by the SP-0062 startup resume: the stream starts exactly as it would on a click,
    // but nothing this route can raise is allowed to be a modal window. A dialog answers an action the
    // user just took; at launch it is an ambush, and with several streams resumed they would stack.
    // SP-0086: randomHunt marks the random-station hunt's own calls. It suppresses exactly two things,
    // both meaning "this is not a user asking for this channel": the stop-toggle below - an independent
    // draw may name the station already playing, and the command must never answer with silence - and the
    // hunt cancellation, which every other caller does trigger because every other caller is a user or
    // system decision that supersedes a hunt in flight. Cancelling here rather than in StopAudioPlayback
    // is deliberate: that funnel is also the hunt's own between-attempt stop, so a hook there would make
    // the hunt cancel itself after its first attempt.
    private async Task PlayChannelAsync(StreamChannel channel, bool rememberSelection, bool startFullscreen = false, bool quiet = false, bool randomHunt = false)
    {
        if (!randomHunt && channel.MediaKind == MediaKind.Audio && _playingAudio?.Channel.Id == channel.Id)
        {
            StopAudio();
            return;
        }

        if (!randomHunt)
        {
            CancelRandomStationHunt();
        }

        if (rememberSelection)
        {
            await RememberSelectedChannelAsync(channel.Id);
        }

        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            _log.Event("REFUSE", "op=playback", "reason=offline", $"kind={channel.MediaKind}", $"url={channel.Url}");
            if (quiet)
            {
                SetStatus("ResumeSkippedOffline");
            }
            else if (IsCompact)
            {
                // SP-0080: see IsCompact - a modal owned by the hidden catalog would sit under the panel.
                SetStatus("OfflinePlayback");
            }
            else
            {
                MessageBox.Show(this, LocalizationService.Get("OfflinePlayback"), LocalizationService.Get("ProductName"));
            }

            return;
        }

        if (channel.MediaKind == MediaKind.Audio)
        {
            // Assigned on every audio start, so an ordinary user play is what clears a resume's quiet
            // session - there is no separate path that has to remember to forget it.
            _audioQuiet = quiet;
            StopAudioPlayback();
            _audioNavOrder = CaptureAudioNavOrder();
            _currentTrackText = null;
            _audioRecovery = new LivePlaybackRecoveryPolicy();
            _audioRecoveryCts = new CancellationTokenSource();
            // The same set the list is built from: a row already cached here must not have its atlases
            // narrowed by the act of starting playback, and a row created here for an externally
            // launched channel carries no favicon index to resolve in the first place.
            _playingAudio = GetOrCreateRow(channel, BuildFaviconAtlasSet());
            _playingAudio.SetPlayingAudio(true);
            // System-only wake: keep the machine awake while the radio plays, but let the display
            // turn off normally (Decision 3). Held across bounded reconnects; released in StopAudioPlayback.
            _audioWake = WakeGuard.Acquire(keepDisplayOn: false);
            _ = SuspendPreviewsAsync();
            SetNowPlaying("ConnectingAudio", StreamTitleFormatter.Display(channel.Title));
            StartAudioPlayback(channel, reconnecting: false);
            EnsureSystemMediaControls();
            PublishAudioSession(playing: true);
            // SP-0062: the one place a station session opens. A recovery leg re-enters StartAudioPlayback
            // with reconnecting: true and never comes through here, which is what makes "a reconnect writes
            // nothing" true by construction rather than by a guard.
            await NoteStreamStartedAsync(channel.Id);
        }
        else
        {
            OpenIndependentPlayerWindow(channel, startFullscreen, quiet);
        }
    }

    // Applies the audio-volume preference and starts (or, on a recovery reconnect, restarts) the MediaElement
    // session for the channel. The caller sets the Connecting/Reconnecting now-playing label.
    private void StartAudioPlayback(StreamChannel channel, bool reconnecting)
    {
        _log.Event(reconnecting ? "AUDIO RECONNECT" : "AUDIO OPEN", $"url={channel.Url}");
        if (reconnecting)
        {
            NoteAudioReconnectLeg();
        }
        else
        {
            BeginAudioSession(channel);
        }

        _suppressAudioVolumeSave = true;
        AudioVolumeSlider.Value = _state.AudioVolume;
        _suppressAudioVolumeSave = false;
        AudioPlayer.Volume = _state.AudioVolume / 100.0;
        AudioVolumeSlider.Visibility = Visibility.Visible;
        AudioPlayer.Source = new Uri(channel.Url);
        AudioPlayer.Play();
        ApplyAudioTransportState();
        StartNowPlayingMetadata(channel);
    }

    private async void AudioPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_playingAudio is null)
        {
            return;
        }

        SetNowPlaying("NowPlaying", _playingAudio.DisplayTitle);
        _log.Event("AUDIO LIVE", $"url={_playingAudio.Channel.Url}");
        NoteAudioLive();
        // SP-0062: a resumed station that has been live once is an ordinary station, so its later failures
        // get the ordinary dialog. Cleared here rather than tested against a liveness field, because the
        // recovery path resets those on every leg and the session would stay silent forever.
        _audioQuiet = false;
        // SP-0086: the hand-off. From this line the station is an ordinary station with the recovery
        // policy PlayChannelAsync installed, and the hunt that started it is over.
        if (_randomHunt is { } hunt && hunt.ProbeChannelId == _playingAudio.Channel.Id)
        {
            hunt.Outcome.TrySetResult(true);
        }

        _audioRecovery?.NotifyLive(); // sustained live - restore the full recovery budget
        await RecordPlayOutcome(_playingAudio.Channel.Id, true);
    }

    private async void AudioPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        var row = _playingAudio;
        var reason = e.ErrorException?.GetType().Name ?? "unknown";
        _log.Event("AUDIO FAIL", $"reason={reason}", $"url={row?.Channel.Url ?? "n/a"}");
        if (row is null)
        {
            return;
        }

        if (YieldToRandomStationHunt(row.Channel, reason))
        {
            return;
        }

        // Stop the failed session but keep the recovery policy/CTS alive so this channel can reconnect.
        AudioPlayer.Stop();
        AudioPlayer.Source = null;
        await RecoverAudioAsync(row.Channel, reason);
    }

    /// <remarks>
    /// SP-0069: <c>MediaElement</c> reports a server that closed the response *cleanly* as MediaEnded,
    /// not MediaFailed - and nothing was listening, so the session simply never ended. What stayed behind
    /// was worse than a leak: <c>_audioWake</c> kept forbidding the machine to sleep, the sleep timer kept
    /// counting, the Windows media session kept showing Playing and the now-playing line kept naming a
    /// station that had stopped. A station that ends is the ordinary way a relay drops, so this takes the
    /// same bounded path video already takes for the same event
    /// (<see cref="PlayerWindow"/>'s EndReached handler): reconnect within the StreamEnded budget, and
    /// once that budget is spent fail terminally - which is the funnel that finally releases the hold.
    /// </remarks>
    private async void AudioPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        var row = _playingAudio;
        _log.Event("AUDIO ENDED", $"url={row?.Channel.Url ?? "n/a"}");
        if (row is null)
        {
            return;
        }

        if (YieldToRandomStationHunt(row.Channel, "end_reached"))
        {
            return;
        }

        AudioPlayer.Stop();
        AudioPlayer.Source = null;
        await RecoverAudioAsync(row.Channel, "end_reached", endReached: true);
    }

    // Bounded audio recovery (streams.txt Part D). Classifies the failure, then reconnects after a cancellable
    // backoff (showing a Reconnecting label) or, once the budget is spent or a hard failure is hit, shows the
    // terminal dialog. There is no position stall-watchdog for audio: MediaElement exposes no live telemetry.
    private async Task RecoverAudioAsync(StreamChannel channel, string reason, bool endReached = false)
    {
        var policy = _audioRecovery;
        var cts = _audioRecoveryCts;
        if (policy is null || cts is null || cts.IsCancellationRequested || _playingAudio?.Channel.Id != channel.Id)
        {
            return; // audio was stopped or switched to another channel
        }

        // Only a fresh open failure needs the status probe; a stream that ended already carries its own
        // signal, and probing it would spend a request to learn nothing. Same rule the video path applies.
        int? status = endReached ? null : await PlaybackStatusProbe.TryGetStatusAsync(channel.Url, cts.Token);
        if (cts.IsCancellationRequested || _playingAudio?.Channel.Id != channel.Id)
        {
            return; // stopped or switched while probing - do not relabel or restart
        }

        var decision = policy.Decide(new PlaybackFailureSignal(reason, EndReached: endReached, HttpStatusCode: status));
        _log.Event("AUDIO RECOVER",
            $"trigger={decision.Trigger}",
            $"action={decision.Kind}",
            $"attempt={decision.Attempt}",
            $"budget={decision.Budget}",
            $"delay_ms={decision.Delay.TotalMilliseconds:F0}",
            $"reason={reason}",
            $"http={status?.ToString() ?? "n/a"}",
            $"url={channel.Url}");

        if (decision.Kind == RecoveryActionKind.HardFail)
        {
            await FailAudioTerminallyAsync(channel, reason);
            return;
        }

        SetNowPlaying("ReconnectingAudioAttempt", StreamTitleFormatter.Display(channel.Title), decision.Attempt, decision.Budget);
        try
        {
            await Task.Delay(decision.Delay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // stop / switch / close cancelled the wait - never restart the old station
        }

        if (cts.IsCancellationRequested || _playingAudio?.Channel.Id != channel.Id)
        {
            return;
        }

        StartAudioPlayback(channel, reconnecting: true);
    }

    // Terminal audio failure: record the real failed play (red status) and offer Retry / Copy / Hide|Delete / Keep.
    private async Task FailAudioTerminallyAsync(StreamChannel channel, string reason)
    {
        NoteAudioTerminalFailure(); // before the stop, which is what closes and records the session
        var quiet = _audioQuiet; // StopAudio below reassigns nothing, but the next play would
        StopAudio();
        await RecordPlayOutcome(channel.Id, false);
        if (quiet)
        {
            // SP-0062: a stream resumed at launch that never reached live reports itself in the status line.
            // Everything above this point - the outcome record, the session log - is identical to a click.
            SetStatus("ResumeStreamFailed");
            return;
        }

        // SP-0080: see IsCompact. The panel is the only surface on screen, and it is the surface the
        // ticket chose over a window that jumps in front of the listener's other work - so a station
        // that could not be brought back says so on the line the panel mirrors, and nothing pops up.
        if (IsCompact)
        {
            SetStatus("CompactPanelStreamFailed", StreamTitleFormatter.Display(channel.Title));
            return;
        }

        var report = FailureReportFormatter.Format(new FailureReport(
            ProductInfo.Version,
            DateTimeOffset.UtcNow,
            channel.Title,
            channel.Url,
            channel.MediaKind,
            PlaybackErrorClassifier.Classify(reason)));
        var dialog = new PlaybackFailureDialog(channel.Title, channel.SourceOrigin, report, channel.Access) { Owner = this };
        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case PlaybackFailureChoice.Retry:
                await PlayChannelAsync(channel, rememberSelection: false);
                break;
            case PlaybackFailureChoice.Remove:
                await RemoveChannelAsync(channel);
                break;
        }
    }

    private async void AudioVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The slider's XAML Value="100" fires ValueChanged during InitializeComponent,
        // before the AudioPlayer element below it in the tree exists. Ignore that spurious fire.
        if (AudioPlayer is null)
        {
            return;
        }

        var volume = (int)Math.Round(e.NewValue);
        AudioPlayer.Volume = volume / 100.0;
        UpdateCompactPanel(); // SP-0080: before the early returns below - the panel mirrors the position, not the save
        if (_suppressAudioVolumeSave)
        {
            return;
        }

        if (_state.AudioVolume == volume)
        {
            return;
        }

        _state = await PersistAsync(_state with { AudioVolume = volume });
    }

    // SP-0081: the panel's own transport. Silencing a station is the common reason to press this, and
    // ending the session is not what that asks for - so the button stops the sound and keeps the station,
    // then offers it back. A real stop is still one click away on the playing row, on the system flyout's
    // Stop, and in starting another station.
    private void AudioTransportButton_Click(object sender, RoutedEventArgs e) => ToggleAudioTransport();

    // SP-0080: extracted from the handler so the compact panel's transport button reaches the same
    // decision rather than restating it. A second copy of "playing means pause" is exactly how the two
    // surfaces would come to disagree about what the button does.
    private void ToggleAudioTransport()
    {
        if (_playingAudio is not null)
        {
            PauseAudio();
        }
        else
        {
            ResumeAudio();
        }
    }

    /// <summary>
    /// Puts the panel's transport button, volume and sleep timer into the state the audio session is
    /// actually in. Reading both fields here, rather than showing and hiding them at each call site, is
    /// what keeps the paused state - a stopped session with a remembered station - from looking like
    /// nothing playing: <see cref="StopAudioPlayback(bool)"/> runs on the way into it as well.
    /// </summary>
    private void ApplyAudioTransportState()
    {
        var playing = _playingAudio is not null;
        var hasStation = playing || _audioPausedChannel is not null;
        AudioTransportButton.IsEnabled = hasStation;
        AudioTransportButton.Visibility = hasStation ? Visibility.Visible : Visibility.Collapsed;
        AudioVolumeSlider.Visibility = hasStation ? Visibility.Visible : Visibility.Collapsed;
        // SP-0080: the panel exists for an audio session, and after SP-0081 a stopped-but-remembered
        // station is still one - which is what keeps criterion 4's "stop while collapsed" from removing
        // the way back in.
        CompactPanelButton.Visibility = hasStation ? Visibility.Visible : Visibility.Collapsed;
        ShowSleepTimerControl(hasStation);
        AudioTransportButton.Style = (Style)FindResource(playing ? "StopGlyphButton" : "PlayGlyphButton");
        // A resource reference rather than an assigned string, so the caption follows a language change
        // on its own - this window is open across every one of them.
        AudioTransportButton.SetResourceReference(ContentControl.ContentProperty, playing ? "StopAudio" : "ResumeAudio");
        UpdateCompactPanel();
    }

    private void StopAudio()
    {
        // A user-initiated stop ends the sleep timer too (SP-0022); an internal stop for a station
        // switch goes through StopAudioPlayback directly and keeps the deadline.
        // SP-0086: and for the same reason it ends a random-station hunt. The hunt's own between-attempt
        // stop takes the StopAudioPlayback route below, so it cannot cancel itself here.
        CancelRandomStationHunt();
        CancelSleepTimer(announce: false);
        StopAudioPlayback();
        _ = StartPreviewsAsync();
    }

    private void StopAudioPlayback() => StopAudioPlayback(clearSystemSession: true);

    // clearSystemSession is false only for the SP-0021 pause path, which stops the live session but
    // keeps the Windows media session visible as Paused so a later system Play can resume the channel.
    private void StopAudioPlayback(bool clearSystemSession)
    {
        EndAudioSession(); // SP-0040: this is the one funnel every stop, switch, pause and failure passes through
        // SP-0062: and therefore the one place a station leaves the resume record - including the SP-0021
        // pause, because a paused session is deliberately not something the next launch brings back.
        var stoppedChannelId = _playingAudio?.Channel.Id;
        _audioRecoveryCts?.Cancel(); // cancel any in-flight recovery backoff (stop / switch / close)
        _audioRecoveryCts?.Dispose();
        _audioRecoveryCts = null;
        _audioRecovery = null;
        StopNowPlayingMetadata();
        _audioWake?.Dispose(); // release the idle-sleep hold on every stop/switch/toggle/terminal-fail path
        _audioWake = null;
        AudioPlayer.Stop();
        AudioPlayer.Source = null;
        _playingAudio?.SetPlayingAudio(false);
        _playingAudio = null;
        SetNowPlaying("NothingPlaying");
        if (clearSystemSession)
        {
            _audioPausedChannel = null;
            ClearSystemMediaSession();
        }

        // SP-0081: after the fields above, so the panel reports the state this stop actually left behind.
        // The pause path re-runs it once it has recorded its station, which is what turns the controls
        // back on for a session that is stopped but not over.
        ApplyAudioTransportState();

        if (stoppedChannelId is { } id)
        {
            _ = NoteStreamStoppedAsync(id);
        }
    }

    private void OpenIndependentPlayerWindow(StreamChannel channel, bool startFullscreen = false, bool quiet = false)
    {
        var window = new PlayerWindow(
            channel,
            _log,
            RecordPlayOutcome,
            RemoveChannelAsync,
            () => _state.Channels.FirstOrDefault(item => item.Id == channel.Id)?.Pinned ?? channel.Pinned,
            pinned => SetChannelPinnedAsync(channel, pinned),
            _state.PlayerWindowTopmost,
            SetPlayerTopmostAsync,
            () => _state.Collections,
            (collectionId, member) => SetCollectionMembershipAsync(collectionId, channel.Id, member),
            name => CreateCollectionWithChannelAsync(name, channel.Id),
            _state.VideoVolume,
            _state.VideoMuted,
            SaveVideoAudioPreferencesAsync,
            (url, frame) => _previewCoordinator?.IngestFrame(url, frame),
            () => _state.FrameFolder,
            _state.VideoBackend,
            startFullscreen,
            quiet) { Owner = this };
        _openPlayerWindows++;
        _playerWindows.Add(window);
        _ = SuspendPreviewsAsync();
        // SP-0062: an open player window is a playing stream. A window that never reached live is still one
        // the user chose to have open, and treating it otherwise would need a liveness signal PlayerWindow
        // does not expose.
        _ = NoteStreamStartedAsync(channel.Id);
        window.Closed += async (_, _) =>
        {
            _openPlayerWindows = Math.Max(0, _openPlayerWindows - 1);
            _playerWindows.Remove(window);
            await NoteStreamStoppedAsync(channel.Id);

            // Closing the player leaves activation with whatever application sits next in the z-order
            // instead of returning it to this owner, so the catalog dropped behind unrelated windows and
            // had to be fished out of the taskbar. Only the last player window does this, so closing one
            // of several never pulls focus off the ones still playing, and a minimized or hidden catalog
            // is left where the user put it.
            if (_openPlayerWindows == 0 && IsVisible && WindowState != WindowState.Minimized)
            {
                Activate();
            }

            await StartPreviewsAsync();
        };
        // The owner above is lent for the CenterOwner placement and taken back as soon as the window is
        // placed - see PlayerWindow_Loaded for why the player must not stay an owned window.
        window.Show();
    }

    // ToArray: every Close raises the Closed handler registered above, which mutates the set.
    private void CloseOpenPlayerWindows()
    {
        foreach (var window in _playerWindows.ToArray())
        {
            window.Close();
        }
    }

    private async Task SuspendPreviewsAsync()
    {
        if (_previewCoordinator is not null)
        {
            await _previewCoordinator.StopAsync();
        }
    }

    private async Task RecordPlayOutcome(Guid id, bool succeeded)
    {
        var channel = _state.Channels.FirstOrDefault(item => item.Id == id);
        if (channel is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        ReplaceChannel(channel with
        {
            LastPlayOutcome = succeeded ? PlayOutcome.Ok : PlayOutcome.Fail,
            LastPlayOutcomeAt = now,
            LastPlayedAt = succeeded ? now : channel.LastPlayedAt
        });

        // SP-0019: a history entry is created only at the successful-play sink; failed attempts
        // (and the preview/probe paths, which never reach here) never create or promote one.
        if (succeeded)
        {
            _state = _state with
            {
                ListeningHistory = ListeningHistory.RecordPlay(
                    _state.ListeningHistory, channel.Id, channel.Title, channel.MediaKind, now)
            };
        }

        _state = await PersistAsync(_state);
        ApplyFilter();
    }

    // Maps every user-editable field from the Add/Edit dialog onto a channel. MediaKind falls back
    // to URL classification when the dialog leaves it on "Auto". Identity/provenance fields are untouched.
    private static StreamChannel ApplyDialogMetadata(StreamChannel channel, AddStreamWindow dialog, string url, string title) =>
        channel with
        {
            Url = url,
            Title = title,
            MediaKind = dialog.SelectedMediaKind ?? StreamMediaKindClassifier.Classify(url),
            Category = dialog.MetaCategory,
            Topic = dialog.MetaTopic,
            Language = dialog.MetaLanguage,
            Country = dialog.MetaCountry,
            Homepage = dialog.MetaHomepage,
            Protocol = dialog.MetaProtocol,
            Format = dialog.MetaFormat,
            Bitrate = dialog.MetaBitrate,
            IsLive = dialog.MetaIsLive
        };

    private void ReplaceChannel(StreamChannel replacement)
    {
        var index = _state.Channels.FindIndex(channel => channel.Id == replacement.Id);
        if (index >= 0)
        {
            _state.Channels[index] = replacement;
        }
    }

    private void SetBusy(bool busy, bool cancellable = false)
    {
        _busy = busy;
        // SP-0050: the catalog refresh and add-stream buttons this used to disable are entries in the
        // operations menu now, so the guard moves up to the button that opens it - a second refresh
        // during a refresh stays unreachable.
        OperationsButton.IsEnabled = !busy;
        SettingsButton.IsEnabled = !busy;
        CatalogProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        // SP-0056: four operations share this bar and only two of them report anything, so the
        // indeterminate default is restored on both edges. Entering covers the operations that never
        // report; leaving stops a reporting one from leaking its determinate mode into the next, even
        // when it left by throwing.
        CatalogProgress.IsIndeterminate = true;
        CatalogProgress.Value = 0;
        // A wait cursor overrides every element's own cursor, so a cancel button under one reads as
        // disabled. A cancellable operation shows real progress and a real affordance instead.
        Mouse.OverrideCursor = busy && !cancellable ? Cursors.Wait : null;
        CancelOperationButton.Visibility = busy && cancellable ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.IsEnabled = busy && cancellable;
    }

}
