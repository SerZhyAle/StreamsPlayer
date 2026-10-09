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

    // SP-0056: dedicated, with no timeout of its own. SP-0129 settled what HttpClient.Timeout covers -
    // the wait for the response head only, never a body read after it (HttpClientTimeoutPremiseTests) - so
    // every service on this client bounds its head explicitly (HttpDownload.SendForHeadersAsync) and its
    // body with a silence bound. A client timeout here would add nothing but a second, unlabelled limit.
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
    private CatalogStateCommitter? _stateCommitter;
    private BrowsingSession _session = new();
    private ChannelRow? _playingAudio;
    private StreamAudioRecorder? _audioRecorder;
    private LivePlaybackRecoveryPolicy? _audioRecovery;
    private CancellationTokenSource? _audioRecoveryCts;
    private IDisposable? _audioWake;
    private bool _suppressAudioVolumeSave;
    private int? _pendingAudioVolume;
    private bool _audioOutcomeRecorded;
    private bool _audioTerminalFailureRecorded;
    private ChannelRow? _selectedRow;
    private bool _busy;
    private bool _toolsActionActive;
    // SP-0059: whether this launch found no state file at all. Read once, before the load.
    private bool _cleanInstall;
    // SP-0175: whether this launch's catalog load failed. While set, nothing may offer or perform a
    // write to the state file - the store refuses the writes, and the snapshot offer is suppressed
    // until a read has succeeded, which in practice is the restart the load notice asks for.
    private bool _catalogStateUnreadable;
    private bool _isGridMode;
    private bool _windowActive = true;
    private int _openPlayerWindows;
    private int _catalogColumns = 1;
    private CancellationTokenSource? _viewportDebounce;
    private CancellationTokenSource? _hoverDwell;
    // SP-0065: this window is closing, so the preview subsystem is closed for business. The dispatcher pumps
    // input while the close work saves, so events keep arriving right through the teardown that disposes the
    // two sources above. Every preview entry point reads it; see MainWindow.Previews.cs. SP-0120: set in
    // MainWindow_Closing, before the player windows are closed - the last player's Closed callback is a
    // preview entry point too, and with the latch still clear it restarted capture during shutdown.
    private bool _shuttingDown;
    // SP-0120: the engine releases of the players closed by quitting; the close work waits for them (bounded).
    private readonly List<Task> _closedPlayerEngines = [];
    // SP-0164: the recording finishes of the players closed by quitting, each already bound to its engine
    // release; the close work waits for them too, so the move into the recordings folder is given its chance.
    private readonly List<Task> _closedRecordingFinishes = [];
    // SP-0067: collapses a burst of events into one save. The interval is also the value the scroll-only
    // rate limit restores when it hands the timer back; see MainWindow.BrowsingSession.cs.
    private static readonly TimeSpan BrowsingSessionSaveDebounce = TimeSpan.FromMilliseconds(350);
    private readonly DispatcherTimer _browsingSessionSaveTimer;
    private static readonly TimeSpan AudioVolumeSaveDebounce = TimeSpan.FromMilliseconds(600);
    private readonly DispatcherTimer _audioVolumeSaveTimer;
    // SP-0096: the radio's half of the open budget. A timer rather than PlaybackOpenBudget itself:
    // the rule was written when radio ran on WPF MediaElement, which publishes no counters, so it
    // collapsed to its deadline and the dead-source branch has no input here. The LibVLC audio engine
    // (SP-0104) does expose statistics now, but only the audible-output proof (SP-0133) reads them;
    // the open budget still waits for Playing alone. The interval comes from Core so the radio and
    // the player cannot drift apart.
    private readonly DispatcherTimer _audioOpenTimer;
    // SP-0104: LibVLC-based audio playback engine for standard radio streams. Built in the constructor, where
    // the log it reports hung-engine abandonments to (SP-0165) already exists - field initializers run first.
    private readonly StandardAudioPlayback _standardAudioPlayback;
    private bool _restoringBrowsingSession;
    private bool _resettingFilters;
    // SP-0067: a pixel offset read off the scroll event, not a channel identity searched for among the
    // rows. See RestoreScrollAnchorAsync for why approximate is the accepted answer here.
    private double _lastScrollOffset;
    // When the session was last actually written. Read by SaveBrowsingSessionAsync to hold a save that
    // carries nothing but a new scroll position to its own, coarser interval.
    private DateTimeOffset _lastBrowsingSessionWriteUtc;
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
        _browsingSessionSaveTimer = new DispatcherTimer { Interval = BrowsingSessionSaveDebounce };
        _browsingSessionSaveTimer.Tick += BrowsingSessionSaveTimer_Tick;
        _audioVolumeSaveTimer = new DispatcherTimer { Interval = AudioVolumeSaveDebounce };
        _audioVolumeSaveTimer.Tick += AudioVolumeSaveTimer_Tick;
        _audioOpenTimer = new DispatcherTimer { Interval = PlaybackOpenBudget.OpenDeadline };
        _audioOpenTimer.Tick += AudioOpenTimer_Tick;
        _standardAudioPlayback = new StandardAudioPlayback((tag, fields) => _log.Event(tag, fields));
        _standardAudioPlayback.Playing += StandardAudioPlayback_Playing;
        _standardAudioPlayback.Ended += StandardAudioPlayback_Ended;
        _standardAudioPlayback.Failed += StandardAudioPlayback_Failed;
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
            var captureService = TryStartGridPreviewEngine();
            _previewCoordinator = captureService is null ? null : new GridPreviewCoordinator(
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
        // SP-0125: the loader is static and outlives this window, so the subscription is dropped on close.
        FaviconTileLoader.AtlasReady += FaviconTileLoader_AtlasReady;
        Closed += (_, _) => FaviconTileLoader.AtlasReady -= FaviconTileLoader_AtlasReady;
    }

    private void FaviconTileLoader_AtlasReady()
    {
        foreach (var row in _rowCache.Values)
        {
            row.RefreshPendingFavicon();
        }
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
        try
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
                // SP-0179: the single folder choice is split per capture kind in memory; the next save keeps it.
                _state = CaptureFolderChoices.Split(await _store.LoadAsync());
            }
            catch (Exception exception)
            {
                // SP-0116 chose a fresh start over an unreadable catalog; SP-0175 adds the other half:
                // the file on disk is preserved. The store refuses every save until a read has
                // succeeded, so the notice's "restart the app to try again" is now literally the way
                // back, and no action or offer that writes the file may appear in the meantime.
                _catalogStateUnreadable = true;
                _log.Error("Catalog state load failed", exception);
                SetStatus("MainLoadFailed");
                MessageBox.Show(this, LocalizationService.Get("MainLoadFailedBody"), LocalizationService.Get("ProductName"), MessageBoxButton.OK, MessageBoxImage.Error);
            }

            _preferencesLoaded = true;
            _stateCommitter = CreateStateCommitter(_state);
            try
            {
                // SP-0067: right after the catalog, and given it as the migration source. When the session
                // file is absent this is the one read of the old CatalogState fields, ever - it writes the
                // new file in the same call, so the next launch never looks at them again. A migration
                // save that fails defers to the next launch (SP-0175); it can no longer fail this load.
                _session = await _sessionStore.LoadAsync(
                    _state,
                    onMigrationSaveFailure: exception => _log.Error("Browsing-session migration save deferred to the next launch", exception));
                // SP-0084: read once here so that placing a player window later needs no await - a window
                // cannot be positioned after it is visible without the user seeing it jump. Its own file, so
                // a failure costs window placements and nothing else.
                await PlayerGeometryFile.PrimeAsync();
                ThemeService.Apply(_state.Theme);

                // SP-0034 decision 5: no saved preference means a fresh install, so follow the operating
                // system and fall back to English. A preference that is present is always honoured.
                var savedLanguage = _state.Language;
                var language = savedLanguage ?? InterfaceLanguages.Detect(
                    CultureInfo.CurrentUICulture,
                    CultureInfo.InstalledUICulture);
                LocalizationService.Apply(language);
                WakeGuard.Enabled = _state.KeepAwakeDuringPlayback;
                _standardAudioPlayback.AudioOutputDevice = _state.AudioOutputDevice;
                _standardAudioPlayback.AudioChannelMode = _state.AudioChannelMode;
                Topmost = _state.MainWindowTopmost;
                if (savedLanguage is null && !_catalogStateUnreadable)
                {
                    // Record the detected language once, so the next launch is an ordinary
                    // saved-preference launch and a later OS change cannot silently move the interface.
                    // Skipped after a failed load: the store would refuse the write anyway, and the
                    // next successful launch asks the question again (SP-0175).
                    await PersistAsync(state => state with { Language = language });
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
                if (!_catalogStateUnreadable)
                {
                    // The failure path keeps its MainLoadFailed status and its error log; a fresh
                    // catalog reported as loaded would contradict both (SP-0175).
                    _log.Information($"Catalog state loaded: {_state.Channels.Count} channel(s).");
                    SetCatalogStatus();
                }
            }
            catch (Exception exception)
            {
                // SP-0175: a failure past the catalog load is this step's own. The catalog in memory is
                // fine, and reporting it as a load failure would tell the user to restart away a
                // catalog he still has; the boundary logs the fault under this step's name and shows
                // the handler notice.
                HandlerBoundary.Report("MainWindow_Loaded.ViewSetup", exception);
            }
            finally
            {
                SetBusy(false);
            }

            // SP-0170: each start-up step is its own failure boundary. A fault in the previews or the TV schedule
            // costs that feature and is reported, but no longer keeps the requested playback - or the launches
            // another copy forwarded meanwhile - from running.
            if (IsGridMode)
            {
                await RunStartupStepAsync(nameof(StartPreviewsAsync), StartPreviewsAsync);
            }

            await RunStartupStepAsync(nameof(LoadTvScheduleAsync), LoadTvScheduleAsync); // SP-0075: the local file only - never the network
            await StartRequestedPlaybackAsync();
            await HandOverStagedRecordingsAsync(); // SP-0121: what a crash left in staging goes to the user

            // SP-0132: both through the gate. A named launch or a resume starts a station above, which makes
            // the panel reachable before these questions are asked.
            if (_cleanInstall)
            {
                // SP-0059: a machine that has never run the product is asked where its channels come from.
                await WhenCatalogShownAsync(AskWhereChannelsComeFromAsync);
            }
            else
            {
                // SP-0052: the one first-launch offer, shown after the window has settled so it never
                // competes with the load itself. Nothing is applied until the user accepts it. Kept exactly
                // as it was for every installation that already has local state.
                await WhenCatalogShownAsync(OfferCatalogSnapshotAsync);
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(MainWindow_Loaded), exception);
        }
        finally
        {
            // SP-0170: the last resort. A start-up that failed before the requested playback never opened the gate
            // forwarded launches wait at; they would wait for ever. A no-op when it was opened.
            await DrainForwardedLaunchesAsync();
        }
    }

    /// <summary>SP-0170: one start-up step behind its own boundary; a fault is reported and the next step still runs.</summary>
    private static async Task RunStartupStepAsync(string step, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report($"MainWindow_Loaded.{step}", exception);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => HandlerBoundary.Run(nameof(RefreshButton_Click), () => RefreshCatalogAsync());

    /// <summary>
    /// The catalog import. SP-0059 gave it a name of its own so the first-launch dialog can await the
    /// same path the button and the menu entry use - including its offline refusal, its cancellation
    /// branch, and the bundled-copy offer that follows a failure.
    /// </summary>
    private async Task RefreshCatalogAsync()
    {
        if (_busy || _toolsActionActive)
        {
            return;
        }

        if (_catalogStateUnreadable)
        {
            // SP-0175: the store refuses every save until a read has succeeded, so a download now would be
            // fetched only to be thrown away at the save.
            _log.Event("REFUSE", "op=catalog_refresh", "reason=state_unreadable");
            MessageBox.Show(this, LocalizationService.Get("MainLoadFailedBody"), LocalizationService.Get("ProductName"), MessageBoxButton.OK, MessageBoxImage.Error);
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
        var imported = false;
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
            var retrying = OnDispatcher<PublishWindowRetryNotice>(notice =>
                ShowPublishWindowRetry(notice, "catalog_refresh", "CatalogPublishWindowRetry"));
            var outcome = await service.DownloadAsync(progress, retrying, _cancellableOperation.Token);
            // SP-0161: the transfer is over, so the run can no longer be stopped - the Cancel button goes
            // dark for the merge and save instead of swallowing a click and reporting success.
            EndCancellablePhase();
            CatalogRefreshResult? result = null;
            var commit = await CommitStateAsync(
                async (state, cancellationToken) =>
                    (result = await StreamCatalogService.ApplyAsync(outcome, state, cancellationToken)).State,
                (state, cancellationToken) => _store.SaveAsync(
                    state,
                    outcome.Bank.FaviconAtlas,
                    outcome.ReplacesAtlas,
                    cancellationToken),
                // SP-0161: merge and save are not cancellable. Closing the window cancels the transfer only; this
                // phase runs to its end so the outcome is either written whole or not at all.
                CancellationToken.None);
            // The download is over and the outcome is about to be written, so no further report may touch
            // the status line.
            _reportingProgress = false;
            if (result is null || !commit.Saved)
            {
                throw commit.Failure ?? new IOException("Catalog refresh was not saved.");
            }
            _log.Information(
                $"Catalog refresh completed: {result.Added} added, {result.Updated} updated, " +
                $"{result.Removed} removed, {result.Retired} retired.");
            if (result.AtlasReplaced)
            {
                _log.Event("CATALOG ATLAS", "op=catalog_refresh", "bank_atlas=present", "installed=replaced");
            }
            else
            {
                // SP-0087: the one refresh outcome that was invisible. The bank carried no usable icon
                // atlas, so the installed one was kept. SP-0088 then made this build's favicon indices be
                // discarded rather than resolved against that older sheet, so what the user sees is
                // monograms where icons were - recoverable by the next refresh, and never another
                // channel's logo. Still nothing on the outside says a download was mispackaged.
                _log.Event("CATALOG ATLAS", "op=catalog_refresh", "bank_atlas=absent", "installed=kept",
                    "indices=discarded", "effect=monogram_until_next_refresh");
            }
            if (result.Retired > 0)
            {
                // SP-0089: rows the bank stopped listing that were kept because the user had authored
                // something on them. Logged separately from `removed` precisely because the two are
                // opposite outcomes, and a support report has to be able to tell "332 channels vanished"
                // from "332 went, none of yours".
                _log.Event("CATALOG RETIRED", "op=catalog_refresh", $"retired={result.Retired}",
                    $"deleted={result.Removed}", "reason=user_authored_state_survives_absence");
            }
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

            imported = true;
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
            failure = FailureCauseText.Describe(exception);
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
        if (failure is not null && !_shuttingDown)
        {
            // SP-0132: through the gate, because the listener may have collapsed to the panel mid-refresh.
            await WhenCatalogShownAsync(() => OfferSnapshotAfterFailedRefreshAsync(failure));
            return;
        }

        // SP-0088: the only path that can lead to a preview-atlas download, and only through the user's
        // answer to the question it asks. Out here for the same reason as the offer above - accepting it
        // runs its own busy cycle, and the modal must not open over a window still showing this one's
        // progress bar.
        if (imported && !_shuttingDown)
        {
            await WhenCatalogShownAsync(OfferChannelPreviewsAsync);
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new AddStreamWindow { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var url = dialog.StreamUrl.Trim();
            if (_state.Channels.Any(channel => CatalogUrlIdentity.SameIdentity(channel.Url, url)))
            {
                MessageBox.Show(this, LocalizationService.Get("DuplicateStream"), LocalizationService.Get("ProductName"));
                return;
            }

            var title = string.IsNullOrWhiteSpace(dialog.StreamTitle) ? LaunchableAddress.HostOf(url) : dialog.StreamTitle.Trim();
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
            await PersistAsync(state => state with { Channels = [.. state.Channels, channel] });
            PopulateFacets();
            ApplyFilter();
            SetStatus("AddedStream", title);

            // The user just typed this address in order to listen to it - start it right away, through the
            // same path a row click uses, so failure handling and history behave identically.
            await PlayChannelAsync(channel, rememberSelection: true);
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(AddButton_Click), exception);
        }
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

    private async void PinButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is not ChannelRow row)
            {
                return;
            }

            await SetChannelPinnedAsync(row.Channel, !row.Channel.Pinned);
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(PinButton_Click), exception);
        }
    }

    // Shared pin/unpin path for the catalog row buttons and the video player's pin button.
    // Pinning moves the channel above every other pinned row (min SortIndex - 1); unpinning keeps its order.
    private async Task SetChannelPinnedAsync(StreamChannel channel, bool pinned)
    {
        await PersistAsync(state =>
        {
            var current = state.Channels.FirstOrDefault(item => item.Id == channel.Id);
            if (current is null || current.Pinned == pinned)
            {
                return state;
            }

            var updated = current with
            {
                Pinned = pinned,
                SortIndex = pinned
                    ? state.Channels.Where(item => item.Pinned).Select(item => item.SortIndex).DefaultIfEmpty(0).Min() - 1
                    : current.SortIndex
            };
            return state with { Channels = [.. state.Channels.Select(item => item.Id == updated.Id ? updated : item)] };
        });
        ApplyFilter();
    }

    private void OverflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChannelRow row } button)
        {
            return;
        }

        OpenChannelMenu(BuildChannelMenu(row, button), button);
    }

    /// <summary>Attaches a per-open channel menu to its button and opens it; the menu lives only while open.</summary>
    /// <remarks>
    /// SP-0132: every item carries its row in its Tag. A pinned-strip container is recycled for another channel
    /// with its button, and a menu left attached would open again on a right-click there and act on the channel
    /// it was built for.
    /// </remarks>
    private static void OpenChannelMenu(ContextMenu menu, Button button)
    {
        button.ContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(button.ContextMenu, menu))
            {
                button.ContextMenu = null;
            }
        };
        menu.IsOpen = true;
    }

    /// <summary>
    /// The per-channel menu shared by the catalog cards and the compact panel (SP-0182): one builder, so the
    /// two surfaces cannot drift apart. Built per open; the row is the channel every item acts on.
    /// </summary>
    private ContextMenu BuildChannelMenu(ChannelRow row, UIElement placementTarget)
    {
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
        // SP-0109: the other half of the shortcut, moved here from Settings.
        var launchCommandItem = new MenuItem
        {
            Header = LocalizationService.Get("MenuCopyLaunchCommand"),
            Tag = row
        };
        launchCommandItem.Click += CopyLaunchCommandMenuItem_Click;
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
        var menu = new ContextMenu { PlacementTarget = placementTarget };
        menu.Items.Add(openItem);
        menu.Items.Add(fullscreenItem);
        menu.Items.Add(newWindowItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(shortcutItem);
        menu.Items.Add(launchCommandItem);
        menu.Items.Add(shareItem);
        menu.Items.Add(editItem);
        menu.Items.Add(aboutItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(pinItem);
        menu.Items.Add(BuildCollectionMenuItem(row));
        if (BuildTvScheduleMenuItem(row) is { } scheduleItem)
        {
            menu.Items.Add(scheduleItem);
        }

        return menu;
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
        try
        {
            if ((sender as MenuItem)?.Tag is ChannelRow row && row.Channel.MediaKind != MediaKind.Audio)
            {
                await PlayChannelAsync(row.Channel, rememberSelection: true, startFullscreen: true);
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(OpenFullscreenMenuItem_Click), exception);
        }
    }

    private async void OpenNewWindowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as MenuItem)?.Tag is ChannelRow row && row.Channel.MediaKind != MediaKind.Audio &&
                !await RefuseUnlaunchableAsync(row.Channel))
            {
                OpenIndependentPlayerWindow(row.Channel);
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(OpenNewWindowMenuItem_Click), exception);
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
        new ChannelInfoWindow(row.Channel, collections, playing is null ? null : playing.DescribeTransmission,
            (tag, fields) => _log.Event(tag, fields))
        {
            Owner = DialogOwner
        }.ShowDialog();
    }

    private async void EditMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as MenuItem)?.Tag is not ChannelRow row)
            {
                return;
            }

            var dialog = new AddStreamWindow(row.Channel) { Owner = DialogOwner };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var url = dialog.StreamUrl.Trim();
            if (_state.Channels.Any(channel => channel.Id != row.Channel.Id && CatalogUrlIdentity.SameIdentity(channel.Url, url)))
            {
                MessageBox.Show(DialogOwner, LocalizationService.Get("DuplicateStream"), LocalizationService.Get("ProductName"));
                return;
            }

            var title = string.IsNullOrWhiteSpace(dialog.StreamTitle) ? LaunchableAddress.HostOf(url) : dialog.StreamTitle.Trim();

            // Editing takes ownership (SP-0126): a published or imported bank row becomes the user's, so no
            // later refresh or import writes over the edit or brings the old address back beside it.
            var channelId = row.Channel.Id;
            var metadata = ApplyDialogMetadata(row.Channel, dialog, url, title);
            await PersistAsync(state => ChannelOwnership.ApplyEdit(
                state,
                channelId,
                owned => ApplyDialogMetadata(owned, metadata)));
            PopulateFacets();
            ApplyFilter();
            SetStatus("EditedStream", title);
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(EditMenuItem_Click), exception);
        }
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
        if (VisualAncestry.IsInsideButton(e.OriginalSource as DependencyObject, (DependencyObject)sender))
        {
            return;
        }

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
        try
        {
            await PlayChannelAsync(row.Channel, rememberSelection: true);
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(Play), exception);
        }
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

        if (await RefuseUnlaunchableAsync(channel, quiet, randomHunt))
        {
            return;
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
            // SP-0110: read before the stop below forgets which station was current or paused.
            var resumesBackdrop = (_playingAudio?.Channel.Id ?? _audioPausedChannelId) == channel.Id;
            StopAudioPlayback();
            CaptureAudioNavOrder();
            _currentTrackText = null;
            _audioRecovery = new LivePlaybackRecoveryPolicy();
            _audioRecoveryCts = new CancellationTokenSource();
            // The same set the list is built from: a row already cached here must not have its atlases
            // narrowed by the act of starting playback, and a row created here for an externally
            // launched channel carries no favicon index to resolve in the first place.
            _playingAudio = GetOrCreateRow(channel, BuildFaviconAtlasSet());
            _playingAudio.SetPlayingAudio(true);
            StartBackdrop(channel.Id, resumesBackdrop);
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
            QueueAudioResumeChange(channel.Id, started: true);
        }
        else
        {
            OpenIndependentPlayerWindow(channel, startFullscreen, quiet);
        }
    }

    // Applies the audio-volume preference and starts (or, on a recovery reconnect, restarts) the audio
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
            _audioOutcomeRecorded = false;
            _audioTerminalFailureRecorded = false;
            BeginAudioSession(channel);
        }

        var volume = _pendingAudioVolume ?? (_stateCommitter?.Requested ?? _state).AudioVolume;
        _suppressAudioVolumeSave = true;
        AudioVolumeSlider.Value = volume;
        _suppressAudioVolumeSave = false;
        AudioVolumeSlider.Visibility = Visibility.Visible;
        if (UsesFastMediaSorterAudioRoute(channel))
        {
            StartFastMediaSorterAudioPlayback(channel, reconnecting);
            return;
        }

        StopFastMediaSorterAudioPlayback();
        // SP-0124: PlayChannelAsync refuses an unlaunchable address before this point. Should one ever get
        // here, nothing is opened and the open budget below ends the session by the ordinary route.
        if (LaunchableAddress.TryParse(channel.Url, out var address))
        {
            _standardAudioPlayback.Play(address, volume);
        }

        // SP-0096: restarted on every leg, so the budget is a per-leg quantity exactly as it is for
        // video. Before this, a station whose URL never opened raised no MediaOpened, no MediaFailed and
        // no MediaEnded, so nothing in this window ever learned that it had not started - the line said
        // "Connecting.." for as long as the user left it there, and _audioWake forbade sleep throughout.
        _audioOpenTimer.Stop();
        _audioOpenTimer.Start();
        ApplyAudioTransportState();
        StartNowPlayingMetadata(channel);
    }

    private void StandardAudioPlayback_Playing(object? sender, StandardAudioEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => HandlerBoundary.Run(nameof(StandardAudioPlayback_Playing), async () =>
        {
            if (IsSupersededAudioEvent(e, "playing"))
            {
                return;
            }

            await HandleAudioOpenedAsync();
            if (IsSupersededAudioEvent(e, "playing_watch"))
            {
                return; // the outcome save above yielded; a stop or replacement during it leaves nothing to watch
            }

            WatchForAudibleOutput(e.Connection); // SP-0133: Playing is not yet sound
            WatchForAudioStall(e.Connection); // SP-0169: nor is it a promise the flow will last
        })), DispatcherPriority.Normal);
    }

    private async Task HandleAudioOpenedAsync()
    {
        // SP-0119: one reading of the playing row. RecordPlayOutcome below is a whole-state save, and Stop,
        // the sleep timer or a hide during it clears the field - reading it again afterwards ended the process.
        if (_playingAudio is not { } playing)
        {
            return;
        }

        // A bounded status document can arrive before the audio engine raises Playing. Preserve that early
        // reading instead of overwriting it with the station name at the exact moment audio becomes live.
        if (string.IsNullOrWhiteSpace(_currentTrackText))
        {
            SetNowPlaying("NowPlaying", playing.DisplayTitle);
        }
        else
        {
            SetNowPlaying("NowPlayingWithTrack", playing.DisplayTitle, _currentTrackText);
        }
        _log.Event("AUDIO LIVE", $"url={playing.Channel.Url}");
        NoteAudioLive();
        StartAudioSmokeRecordingIfAsked(); // SP-0121: the playback smoke gate records through the shipping binary
        // SP-0062: a resumed station that has been live once is an ordinary station, so its later failures
        // get the ordinary dialog. Cleared here rather than tested against a liveness field, because the
        // recovery path resets those on every leg and the session would stay silent forever.
        _audioQuiet = false;
        // SP-0086: the hand-off. From this line the station is an ordinary station with the recovery
        // policy PlayChannelAsync installed, and the hunt that started it is over.
        if (_randomHunt is { } hunt && hunt.ProbeChannelId == playing.Channel.Id)
        {
            hunt.Outcome.TrySetResult(true);
        }

        _audioOpenTimer.Stop(); // SP-0096: it opened, which is the only thing the budget was waiting for
        // SP-0169: live is not yet sustained. The budget comes back when this leg has played long enough
        // (LivePlaybackRecoveryPolicy.SustainedLiveAfter), judged where the leg ends - see RecoverAudioAsync.
        // Resetting it here let a station that connects and drops within seconds reconnect for ever.
        NoteAudioLegLive();
        if (!_audioOutcomeRecorded)
        {
            _audioOutcomeRecorded = true;
            await RecordPlayOutcome(playing.Channel.Id, true);
        }
        if (ReferenceEquals(_playingAudio, playing) && _currentTrackText is { } track)
        {
            QueueNowPlayingHistory(playing.Channel.Id, track);
        }
    }

    private void StandardAudioPlayback_Failed(object? sender, StandardAudioFailedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => HandlerBoundary.Run(nameof(StandardAudioPlayback_Failed), async () =>
        {
            if (IsSupersededAudioEvent(e, "failed"))
            {
                return;
            }

            _audioOpenTimer.Stop(); // the open budget is spent on this leg; the next leg restarts it
            var row = _playingAudio;
            var reason = e.Reason;
            // SP-0189: the engine's own words, redacted at the sink like every other line.
            _log.Event("AUDIO FAIL", $"reason={reason}", $"cause={e.Cause ?? "none"}", $"url={row?.Channel.Url ?? "n/a"}");
            if (row is null)
            {
                return;
            }

            if (YieldToRandomStationHunt(row.Channel, reason))
            {
                return;
            }

            // Stop the failed session but keep the recovery policy/CTS alive so this channel can reconnect.
            _standardAudioPlayback.StopPlayback();
            await RecoverAudioAsync(row.Channel, reason, localEngineFailure: e.LocalEngineFailure);
        })), DispatcherPriority.Normal);
    }

    /// <remarks>
    /// SP-0069: the audio engine reports a server that closed the response *cleanly* as Ended, not
    /// Failed (under WPF <c>MediaElement</c>, before SP-0104, it was MediaEnded) - and nothing used to
    /// listen, so the session simply never ended. What stayed behind
    /// was worse than a leak: <c>_audioWake</c> kept forbidding the machine to sleep, the sleep timer kept
    /// counting, the Windows media session kept showing Playing and the now-playing line kept naming a
    /// station that had stopped. A station that ends is the ordinary way a relay drops, so this takes the
    /// same bounded path video already takes for the same event
    /// (<see cref="PlayerWindow"/>'s EndReached handler): reconnect within the StreamEnded budget, and
    /// once that budget is spent fail terminally - which is the funnel that finally releases the hold.
    /// </remarks>
    private void StandardAudioPlayback_Ended(object? sender, StandardAudioEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => HandlerBoundary.Run(nameof(StandardAudioPlayback_Ended), async () =>
        {
            if (IsSupersededAudioEvent(e, "ended"))
            {
                return;
            }

            _audioOpenTimer.Stop(); // the open budget is spent on this leg; the next leg restarts it
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

            _standardAudioPlayback.StopPlayback();
            await RecoverAudioAsync(row.Channel, "end_reached", endReached: true);
        })), DispatcherPriority.Normal);
    }

    /// <summary>
    /// SP-0096: the station has had the whole open budget and has raised nothing - not MediaOpened, not
    /// MediaFailed, not MediaEnded. Takes exactly the path a reported failure takes from here, which is
    /// the funnel that stops the station, records the outcome, releases the idle-sleep hold and tells
    /// the user; the only thing this handler adds is the fact that the silence has ended.
    /// </summary>
    private async void AudioOpenTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            _audioOpenTimer.Stop();
            if (_playingAudio is not { } row)
            {
                return;
            }

            _log.Event("AUDIO GIVEUP",
                "rule=deadline",
                $"at_ms={PlaybackOpenBudget.OpenDeadline.TotalMilliseconds:F0}",
                $"url={row.Channel.Url}");
            // SP-0086: first, exactly as MediaEnded does it. A hunt in progress owns the outcome of its own
            // probe, and taking it would leave the hunt waiting out its separate connect timeout for a
            // station this rule has already condemned.
            if (YieldToRandomStationHunt(row.Channel, "open_timeout"))
            {
                return;
            }

            _standardAudioPlayback.StopPlayback();
            StopFastMediaSorterAudioPlayback(); // SP-0099: release the watch listener slot during the backoff
            await RecoverAudioAsync(row.Channel, "open_timeout", openTimedOut: true,
                fastMediaSorterFailure: UsesFastMediaSorterAudioRoute(row.Channel) ? FastMediaSorterPlaybackFailureKind.Recoverable : null);
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(AudioOpenTimer_Tick), exception);
        }
    }

    // Terminal audio failure: record the real failed play (red status) and offer Retry / Copy / Hide|Delete / Keep.
    // SP-0099: a FastMediaSorter source names why it ended - the device stopped, or its listener slots are full.
    // SP-0041: the reachability verdict decides whether hide/delete is offered; the default is the old dialog.
    private async Task FailAudioTerminallyAsync(
        StreamChannel channel,
        string reason,
        FastMediaSorterPlaybackFailureKind? fastMediaSorterFailure = null,
        PlaybackReachability reachability = PlaybackReachability.NotProbed)
    {
        NoteAudioTerminalFailure(); // before the stop, which is what closes and records the session
        var quiet = _audioQuiet; // the stop below reassigns nothing, but the next play would
        // SP-0169: not StopAudio. A station that could not be brought back is not the user stopping: the sleep
        // timer's deadline is theirs, and SP-0022 lets only a manual Stop, Cancel or exit clear it - so after
        // Retry the station plays with the timer it had.
        StopAudioKeepingSleepTimer();
        if (!_audioTerminalFailureRecorded)
        {
            _audioTerminalFailureRecorded = true;
            await RecordPlayOutcome(channel.Id, false);
        }
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
        var displayTitle = StreamTitleFormatter.Display(channel.Title);
        string? message = fastMediaSorterFailure switch
        {
            FastMediaSorterPlaybackFailureKind.ListenerLimit => LocalizationService.Format("FmsBroadcastListenerLimit", displayTitle),
            FastMediaSorterPlaybackFailureKind.Recoverable => LocalizationService.Format("FmsBroadcastStopped", displayTitle),
            _ => null
        };
        if (IsCompact)
        {
            switch (fastMediaSorterFailure)
            {
                case FastMediaSorterPlaybackFailureKind.ListenerLimit:
                    SetStatus("FmsBroadcastListenerLimit", displayTitle);
                    break;
                case FastMediaSorterPlaybackFailureKind.Recoverable:
                    SetStatus("FmsBroadcastStopped", displayTitle);
                    break;
                default:
                    SetStatus("CompactPanelStreamFailed", displayTitle);
                    break;
            }

            return;
        }

        var report = FailureReportFormatter.Format(new FailureReport(
            ProductInfo.Version,
            DateTimeOffset.UtcNow,
            channel.Title,
            channel.Url,
            channel.MediaKind,
            PlaybackErrorClassifier.Classify(reason)));
        var dialog = new PlaybackFailureDialog(
            channel.Title, channel.SourceOrigin, report, channel.Access, message, reachability: reachability) { Owner = this };
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
        try
        {
            // The slider's Value="100" raises this while InitializeComponent still runs, before the constructor
            // has created the engine (it now takes the log sink) - nothing to apply yet, the saved volume is
            // applied by the startup path.
            if (_standardAudioPlayback is null)
            {
                return;
            }

            var volume = (int)Math.Round(e.NewValue);
            _standardAudioPlayback.SetVolume(volume);
            _fastMediaSorterAudioPlayback?.SetVolume(volume); // SP-0099: that route plays through LibVLC, not AudioPlayer
            UpdateCompactPanel(); // SP-0080: before the early returns below - the panel mirrors the position, not the save
            if (_suppressAudioVolumeSave)
            {
                return;
            }

            if (_state.AudioVolume == volume && _pendingAudioVolume is null)
            {
                return;
            }

            _pendingAudioVolume = volume;
            _audioVolumeSaveTimer.Stop();
            _audioVolumeSaveTimer.Start();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(AudioVolumeSlider_ValueChanged), exception);
        }
    }

    private async void AudioVolumeSaveTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            _audioVolumeSaveTimer.Stop();
            await FlushPendingAudioVolumeAsync();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(AudioVolumeSaveTimer_Tick), exception);
        }
    }

    private async Task FlushPendingAudioVolumeAsync()
    {
        if (_pendingAudioVolume is not { } volume)
        {
            return;
        }

        _pendingAudioVolume = null;
        await PersistAsync(state => state.AudioVolume == volume ? state : state with { AudioVolume = volume });
    }

    // SP-0081: the panel's own transport. Silencing a station is the common reason to press this, and
    // ending the session is not what that asks for - so the button stops the sound and keeps the station,
    // then offers it back. A real stop is still one click away on the playing row, on the system flyout's
    // Stop, and in starting another station.
    private void AudioTransportButton_Click(object sender, RoutedEventArgs e) => ToggleAudioTransport();

    private void AudioRecordButton_Click(object sender, RoutedEventArgs e) => ToggleAudioRecording();

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
        var hasStation = playing || _audioPausedChannelId is not null;
        var isRecording = _audioRecorder is not null;
        var recordUnavailable = AudioRecordUnavailableReason();
        AudioTransportButton.IsEnabled = hasStation;
        AudioTransportButton.Visibility = hasStation ? Visibility.Visible : Visibility.Collapsed;
        AudioRecordButton.IsEnabled = playing && recordUnavailable is null;
        AudioRecordButton.Visibility = hasStation ? Visibility.Visible : Visibility.Collapsed;
        AudioRecordButton.Style = (Style)FindResource(isRecording ? "StopRecordGlyphOnlyButton" : "RecordGlyphOnlyButton");
        var recordTip = isRecording ? "StopRecordTip" : "RecordTip";
        var recordName = isRecording ? "StopRecord" : "Record";
        AudioRecordButton.SetResourceReference(ToolTipProperty, recordUnavailable ?? recordTip);
        AudioRecordButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, recordName);
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
        CancelSleepTimer(announce: false);
        StopAudioKeepingSleepTimer();
    }

    /// <summary>
    /// Everything a stop does except the user's own deadline. The terminal-failure funnel stops through here:
    /// the sleep timer is cleared only by the actions SP-0022 names (SP-0169).
    /// </summary>
    private void StopAudioKeepingSleepTimer()
    {
        CancelRandomStationHunt();
        StopAudioPlayback();
        _ = StartPreviewsAsync();
    }

    private void StopAudioPlayback() => StopAudioPlayback(clearSystemSession: true);

    // clearSystemSession is false only for the SP-0021 pause path, which stops the live session but
    // keeps the Windows media session visible as Paused so a later system Play can resume the channel.
    private void StopAudioPlayback(bool clearSystemSession)
    {
        StopAudioRecording();
        EndAudioSession(); // SP-0040: this is the one funnel every stop, switch, pause and failure passes through
        // SP-0062: and therefore the one place a station leaves the resume record - including the SP-0021
        // pause, because a paused session is deliberately not something the next launch brings back.
        var stoppedChannelId = _playingAudio?.Channel.Id;
        // SP-0096: this being the one funnel every stop, switch, pause and failure passes through is
        // what makes a single Stop here enough - a station the user stopped must not fail ten seconds
        // later, and a station being switched away from must not condemn its successor.
        _audioOpenTimer.Stop();
        StopWatchingAudioStall(); // SP-0169
        ResetAudioLegTiming();
        _audioRecoveryCts?.Cancel(); // cancel any in-flight recovery backoff (stop / switch / close)
        _audioRecoveryCts?.Dispose();
        _audioRecoveryCts = null;
        _audioRecovery = null;
        StopNowPlayingMetadata();
        _audioWake?.Dispose(); // release the idle-sleep hold on every stop/switch/toggle/terminal-fail path
        _audioWake = null;
        _standardAudioPlayback.StopPlayback();
        StopFastMediaSorterAudioPlayback();
        _playingAudio?.SetPlayingAudio(false);
        StopBackdrop();
        _playingAudio = null;
        SetNowPlaying("NothingPlaying");
        if (clearSystemSession)
        {
            _audioPausedChannelId = null;
            ClearSystemMediaSession();
        }

        // SP-0081: after the fields above, so the panel reports the state this stop actually left behind.
        // The pause path re-runs it once it has recorded its station, which is what turns the controls
        // back on for a session that is stopped but not over.
        ApplyAudioTransportState();

        if (stoppedChannelId is { } id)
        {
            QueueAudioResumeChange(id, started: false);
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
            (_stateCommitter?.Requested ?? _state).PlayerWindowTopmost,
            SetPlayerTopmostAsync,
            () => _state.Collections,
            (collectionId, member) => SetCollectionMembershipAsync(collectionId, channel.Id, member),
            name => CreateCollectionWithChannelAsync(name, channel.Id),
            (_stateCommitter?.Requested ?? _state).VideoVolume,
            (_stateCommitter?.Requested ?? _state).VideoMuted,
            SaveVideoAudioPreferencesAsync,
            (url, frame) => _previewCoordinator?.IngestFrame(url, frame),
            kind => CaptureFolderChoices.For(_state, kind),
            _state.VideoBackend,
            startFullscreen,
            quiet,
            (_stateCommitter?.Requested ?? _state).AudioOutputDevice,
            (_stateCommitter?.Requested ?? _state).AudioChannelMode,
            (_stateCommitter?.Requested ?? _state).KeepPlayerControlsVisible,
            SetKeepPlayerControlsVisibleAsync) { Owner = this };
        AttachTvSchedule(window, channel);
        _openPlayerWindows++;
        _playerWindows.Add(window);
        _ = SuspendPreviewsAsync();
        // SP-0062: an open player window is a playing stream. A window that never reached live is still one
        // the user chose to have open, and treating it otherwise would need a liveness signal PlayerWindow
        // does not expose.
        _ = NoteStreamStartedAsync(channel.Id);
        window.Closed += (_, _) => HandlerBoundary.Run("PlayerWindow.Closed", async () =>
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
        });
        // SP-0084: before Show, so a remembered window appears where it belongs instead of appearing in
        // the centre and then moving. This window is lent as the DPI reference the new one does not have
        // yet; PlayerWindow corrects the result against its own once it has one.
        window.ApplyRememberedPlacement(this);
        // The owner above is lent for the CenterOwner placement and taken back as soon as the window is
        // placed - see PlayerWindow_Loaded for why the player must not stay an owned window. A window
        // placed from memory has already set WindowStartupLocation to Manual and does not use it.
        window.Show();
    }

    // ToArray: every Close raises the Closed handler registered above, which mutates the set. Close raises the
    // player's own Closed synchronously, and that is what starts the engine release read right after it.
    private void CloseOpenPlayerWindows()
    {
        foreach (var window in _playerWindows.ToArray())
        {
            window.Close();
            _closedPlayerEngines.Add(window.EngineReleased);
            // SP-0164: window.Close ran the player's own Closed handler first, so the finish exists by now.
            if (window.CloseRecordingFinish is { } finish)
            {
                _closedRecordingFinishes.Add(finish);
            }
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
        var now = DateTimeOffset.UtcNow;
        var resumeChanges = TakePendingAudioResumeChanges();
        await PersistAsync(state =>
        {
            var withResume = ApplyAudioResumeChanges(state, resumeChanges);
            var channel = state.Channels.FirstOrDefault(item => item.Id == id);
            if (channel is null)
            {
                return withResume;
            }

            var updated = channel with
            {
                LastPlayOutcome = succeeded ? PlayOutcome.Ok : PlayOutcome.Fail,
                LastPlayOutcomeAt = now,
                LastPlayedAt = succeeded ? now : channel.LastPlayedAt
            };
            var history = succeeded
                ? ListeningHistory.RecordPlay(state.ListeningHistory, channel.Id, channel.Title, channel.MediaKind, now)
                : state.ListeningHistory;
            return withResume with
            {
                Channels = [.. state.Channels.Select(item => item.Id == id ? updated : item)],
                ListeningHistory = history
            };
        });
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

    private static StreamChannel ApplyDialogMetadata(StreamChannel channel, StreamChannel metadata) => channel with
    {
        Url = metadata.Url,
        Title = metadata.Title,
        MediaKind = metadata.MediaKind,
        Category = metadata.Category,
        Topic = metadata.Topic,
        Language = metadata.Language,
        Country = metadata.Country,
        Homepage = metadata.Homepage,
        Protocol = metadata.Protocol,
        Format = metadata.Format,
        Bitrate = metadata.Bitrate,
        IsLive = metadata.IsLive
    };

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
