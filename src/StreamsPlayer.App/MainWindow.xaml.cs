using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly string _dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamsPlayer");
    private readonly HttpClient _httpClient;
    private readonly CurrentLog _log;
    private readonly StreamCatalogStore _store;
    private readonly Dictionary<Guid, ChannelRow> _rowCache = [];
    private readonly GridPreviewCoordinator? _previewCoordinator;
    private readonly StreamLaunchRequest _launchRequest;
    private CatalogState _state = new();
    private ChannelRow? _playingAudio;
    private LivePlaybackRecoveryPolicy? _audioRecovery;
    private CancellationTokenSource? _audioRecoveryCts;
    private IDisposable? _audioWake;
    private bool _suppressAudioVolumeSave;
    private ChannelRow? _selectedRow;
    private bool _busy;
    private bool _isGridMode;
    private bool _windowActive = true;
    private int _openPlayerWindows;
    private int _catalogColumns = 1;
    private CancellationTokenSource? _viewportDebounce;
    private CancellationTokenSource? _hoverDwell;
    private readonly DispatcherTimer _browsingSessionSaveTimer;
    private bool _restoringBrowsingSession;
    private Guid? _lastVisibleChannelId;

    internal MainWindow(CurrentLog log, StreamLaunchRequest? launchRequest = null)
    {
        InitializeComponent();
        _log = log;
        _launchRequest = launchRequest ?? new StreamLaunchRequest(StreamLaunchTargetKind.None);
        DataContext = this;
        _store = new StreamCatalogStore(_dataDirectory);
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("StreamsPlayer/0.1");
        _browsingSessionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _browsingSessionSaveTimer.Tick += BrowsingSessionSaveTimer_Tick;
        if (GridPreviewFeature.CaptureEnabled)
        {
            var memoryCache = new PreviewFrameCache(64, url =>
            {
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
            var captureService = new VideoFrameCaptureService();
            _previewCoordinator = new GridPreviewCoordinator(
                Dispatcher,
                GetVisibleRows,
                ApplyPreview,
                memoryCache,
                frameStore,
                captureService,
                url => _log.Event("PREVIEW FAIL", $"url={url}"),
                () => _state.UpdateStreamPreviews);
        }

        UpdateLocalizedOptions();
        Loaded += MainWindow_Loaded;
        Activated += MainWindow_Activated;
        Deactivated += MainWindow_Deactivated;
        Closed += MainWindow_Closed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ChannelRow> Rows { get; } = [];
    public ObservableCollection<CatalogGridRow> GridRows { get; } = [];
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
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SetStatus("MainOpening");
        SetBusy(true);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        try
        {
            _state = await _store.LoadAsync();
            LocalizationService.Apply(_state.Language);
            WakeGuard.Enabled = _state.KeepAwakeDuringPlayback;
            Topmost = _state.MainWindowTopmost;
            MainTopmostCheckBox.IsChecked = _state.MainWindowTopmost;
            LanguageButton.IsEnabled = true;
            MainTopmostCheckBox.IsEnabled = true;
            _preferencesLoaded = true;
            UpdateLocalizedOptions();
            IsGridMode = _state.ViewMode == CatalogViewMode.Grid;
            UpdateViewModeControls();
            InitializeSectionState(_state);
            PopulateFacets();
            RestoreBrowsingSession();
            ApplyFilter();
            UpdateCatalogColumns();
            await RestoreScrollAnchorAsync();
            _log.Information($"Catalog state loaded: {_state.Channels.Count} channel(s).");
            if (_state.LastCatalogRefreshAt is null)
            {
                SetStatus("MainReadyNoRefresh");
            }
            else
            {
                SetStatus("MainLastUpdated", _state.LastCatalogRefreshAt.Value.ToLocalTime());
            }
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
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            _log.Event("REFUSE", "op=catalog_refresh", "reason=offline");
            MessageBox.Show(this, LocalizationService.Get("OfflineCatalog"), LocalizationService.Get("ProductName"));
            return;
        }

        SetStatus("DownloadingCatalog");
        SetBusy(true);
        _log.Information("Catalog refresh started.");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        try
        {
            var service = new StreamCatalogService(_httpClient, _store);
            var result = await service.RefreshAsync(_state);
            _state = result.State;
            _log.Information($"Catalog refresh completed: {result.Added} added, {result.Updated} updated, {result.Removed} removed.");
            PopulateFacets();
            ApplyFilter();
            SetStatus("CatalogResult", result.Added, result.Updated, result.Removed);
            if (IsGridMode && _previewCoordinator is not null)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                await QueueVisibleSafelyAsync(force: true);
            }
        }
        catch (Exception exception)
        {
            _log.Error("Catalog refresh failed", exception);
            SetStatus("CatalogUpdateFailedStatus");
            MessageBox.Show(this, exception.Message, LocalizationService.Get("CatalogUpdateFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
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
        _state = await _store.SaveAsync(_state with { Channels = [.. _state.Channels, channel] });
        PopulateFacets();
        ApplyFilter();
        SetStatus("AddedStream", title);
    }

    private void FilterChanged(object sender, EventArgs e)
    {
        if (IsLoaded && !_updatingLocalizedOptions && !_restoringBrowsingSession)
        {
            ApplyFilter();
            ScrollToCatalogStart();
            ScheduleBrowsingSessionSave();
        }
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void StreamsList_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCatalogColumns();

    private void StreamsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0)
        {
            _lastVisibleChannelId = GetFirstVisibleChannelId();
            ScheduleBrowsingSessionSave();
        }

        if (IsGridMode && e.VerticalChange != 0)
        {
            ScheduleVisiblePreviewUpdate();
        }
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

    private void RebuildGridRows()
    {
        GridRows.Clear();
        foreach (var row in Rows.Chunk(_catalogColumns))
        {
            GridRows.Add(new CatalogGridRow(row, _catalogColumns));
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var category = SelectedOptionValue(CategoryFilter);
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

        IEnumerable<StreamChannel> channels = _state.Channels.Where(channel =>
            !IsHiddenBySet(hiddenIdentities, channel) &&
            (query.Length == 0 || Contains(channel.Title, query) || Contains(channel.Topic, query) || Contains(channel.Language, query)) &&
            (category is null or AllValue || string.Equals(channel.Category, category, StringComparison.OrdinalIgnoreCase)) &&
            (language is null or AllValue || LanguageContains(channel.Language, language)) &&
            (country is null or AllValue || string.Equals(channel.Country, country, StringComparison.OrdinalIgnoreCase)) &&
            (media == AllValue || media == "Audio" && channel.MediaKind == MediaKind.Audio ||
             media == "Video" && channel.MediaKind is MediaKind.Video or MediaKind.Rtsp) &&
            (minBitrateKbps is not int min || StreamBitrate.MeetsMinimum(channel.Bitrate, min)));

        // SP-0025: pinned and unpinned are two distinct sections. Both honour the shared sort (the
        // pinned set no longer keeps a separate SortIndex-only order).
        var pinned = SortChannels(channels.Where(channel => channel.Pinned));
        var unpinned = SortChannels(channels.Where(channel => !channel.Pinned));
        var atlasPath = _store.ResolveAtlasPath(_state);
        var maximumIndex = _state.Channels
            .Where(channel => channel.SourceOrigin == SourceOrigin.Catalog)
            .Select(channel => channel.FaviconIndex)
            .DefaultIfEmpty(null)
            .Max();
        PinnedRows.Clear();
        foreach (var channel in pinned)
        {
            PinnedRows.Add(GetOrCreateRow(channel, atlasPath, maximumIndex));
        }
        Rows.Clear();
        foreach (var channel in unpinned)
        {
            Rows.Add(GetOrCreateRow(channel, atlasPath, maximumIndex));
        }
        RebuildGridRows();

        var totalShown = PinnedRows.Count + Rows.Count;
        EmptyPanel.Visibility = totalShown == 0 ? Visibility.Visible : Visibility.Collapsed;
        StreamsList.Visibility = Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HiddenChannelsButton.Visibility = _state.HiddenCatalogUrls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifySectionState();
        UpdatePinnedSectionLayout();
        if (!_busy)
        {
            var visibleUniverse = hiddenIdentities.Count == 0
                ? _state.Channels.Count
                : _state.Channels.Count(channel => !IsHiddenBySet(hiddenIdentities, channel));
            SetStatus("ChannelCount", totalShown, visibleUniverse);
        }
        ScheduleVisiblePreviewUpdate();
    }

    private ChannelRow GetOrCreateRow(StreamChannel channel, string? atlasPath, int? maximumIndex)
    {
        if (_rowCache.TryGetValue(channel.Id, out var cached))
        {
            cached.UpdatePresentation(atlasPath, maximumIndex);
            cached.UpdateChannel(channel);
            return cached;
        }

        var row = new ChannelRow(channel, atlasPath, maximumIndex);
        _rowCache[channel.Id] = row;
        return row;
    }

    private IEnumerable<StreamChannel> SortChannels(IEnumerable<StreamChannel> channels) =>
        SelectedOptionValue(SortMode) switch
        {
            "Topic" => channels.OrderBy(channel => channel.Topic is null).ThenBy(channel => channel.Topic, StringComparer.OrdinalIgnoreCase),
            "Language" => channels.OrderBy(channel => channel.Language is null).ThenBy(channel => channel.Language, StringComparer.OrdinalIgnoreCase),
            "Country" => channels.OrderBy(channel => channel.Country is null).ThenBy(channel => channel.Country, StringComparer.OrdinalIgnoreCase),
            "Recently added" => channels.OrderByDescending(channel => channel.AddedAt),
            _ => channels.OrderBy(channel => channel.Title, StringComparer.OrdinalIgnoreCase)
        };

    private void PopulateFacets()
    {
        var hiddenIdentities = BuildHiddenIdentitySet();
        IReadOnlyList<StreamChannel> universe = hiddenIdentities.Count == 0
            ? _state.Channels
            : _state.Channels.Where(channel => !IsHiddenBySet(hiddenIdentities, channel)).ToList();
        SetFacet(CategoryFilter, universe.Select(channel => channel.Category));
        SetFacet(LanguageFilter, universe.SelectMany(channel =>
            channel.Language?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? []));
        SetFacet(CountryFilter, universe.Select(channel => channel.Country));
    }

    private static void SetFacet(ComboBox comboBox, IEnumerable<string?> values)
    {
        var selected = SelectedOptionValue(comboBox) ?? AllValue;
        var items = new[] { new UiOption(AllValue, LocalizationService.Get("AllOption")) }.Concat(values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new UiOption(value!, value!))
            .DistinctBy(value => value.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value.Value == AllValue ? string.Empty : value.Label, StringComparer.OrdinalIgnoreCase)).ToList();
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
        _state = await _store.SaveAsync(_state);
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
        var editItem = new MenuItem
        {
            Header = LocalizationService.Get("MenuEdit"),
            Tag = row
        };
        editItem.Click += EditMenuItem_Click;
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
        menu.Items.Add(editItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(pinItem);
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
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            SetStatus("DesktopShortcutFailed");
        }
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

        _state = await _store.SaveAsync(_state);
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
        if (sender is not FrameworkElement { DataContext: ChannelRow row } || ReferenceEquals(_selectedRow, row))
        {
            return;
        }

        _selectedRow?.SetSelected(false);
        _selectedRow = row;
        _selectedRow.SetSelected(true);
        _ = RememberSelectedChannelAsync(row.Channel.Id);
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

    private async Task PlayChannelAsync(StreamChannel channel, bool rememberSelection, bool startFullscreen = false)
    {
        if (channel.MediaKind == MediaKind.Audio && _playingAudio?.Channel.Id == channel.Id)
        {
            StopAudio();
            return;
        }

        if (rememberSelection)
        {
            await RememberSelectedChannelAsync(channel.Id);
        }

        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            _log.Event("REFUSE", "op=playback", "reason=offline", $"kind={channel.MediaKind}", $"url={channel.Url}");
            MessageBox.Show(this, LocalizationService.Get("OfflinePlayback"), LocalizationService.Get("ProductName"));
            return;
        }

        if (channel.MediaKind == MediaKind.Audio)
        {
            StopAudioPlayback();
            _audioNavOrder = CaptureAudioNavOrder();
            _currentTrackText = null;
            _audioRecovery = new LivePlaybackRecoveryPolicy();
            _audioRecoveryCts = new CancellationTokenSource();
            _playingAudio = GetOrCreateRow(channel, _store.ResolveAtlasPath(_state), null);
            _playingAudio.SetPlayingAudio(true);
            // System-only wake: keep the machine awake while the radio plays, but let the display
            // turn off normally (Decision 3). Held across bounded reconnects; released in StopAudioPlayback.
            _audioWake = WakeGuard.Acquire(keepDisplayOn: false);
            _ = SuspendPreviewsAsync();
            SetNowPlaying("ConnectingAudio", StreamTitleFormatter.Display(channel.Title));
            StartAudioPlayback(channel, reconnecting: false);
            EnsureSystemMediaControls();
            PublishAudioSession(playing: true);
        }
        else
        {
            OpenIndependentPlayerWindow(channel, startFullscreen);
        }
    }

    // Applies the audio-volume preference and starts (or, on a recovery reconnect, restarts) the MediaElement
    // session for the channel. The caller sets the Connecting/Reconnecting now-playing label.
    private void StartAudioPlayback(StreamChannel channel, bool reconnecting)
    {
        _log.Event(reconnecting ? "AUDIO RECONNECT" : "AUDIO OPEN", $"url={channel.Url}");
        _suppressAudioVolumeSave = true;
        AudioVolumeSlider.Value = _state.AudioVolume;
        _suppressAudioVolumeSave = false;
        AudioPlayer.Volume = _state.AudioVolume / 100.0;
        AudioVolumeSlider.Visibility = Visibility.Visible;
        AudioPlayer.Source = new Uri(channel.Url);
        AudioPlayer.Play();
        StopAudioButton.IsEnabled = true;
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
        _audioRecovery?.NotifyLive(); // sustained live — restore the full recovery budget
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

        // Stop the failed session but keep the recovery policy/CTS alive so this channel can reconnect.
        AudioPlayer.Stop();
        AudioPlayer.Source = null;
        await RecoverAudioAsync(row.Channel, reason);
    }

    // Bounded audio recovery (streams.txt Part D). Classifies the failure, then reconnects after a cancellable
    // backoff (showing a Reconnecting label) or, once the budget is spent or a hard failure is hit, shows the
    // terminal dialog. There is no position stall-watchdog for audio: MediaElement exposes no live telemetry.
    private async Task RecoverAudioAsync(StreamChannel channel, string reason)
    {
        var policy = _audioRecovery;
        var cts = _audioRecoveryCts;
        if (policy is null || cts is null || cts.IsCancellationRequested || _playingAudio?.Channel.Id != channel.Id)
        {
            return; // audio was stopped or switched to another channel
        }

        var status = await PlaybackStatusProbe.TryGetStatusAsync(channel.Url, cts.Token);
        if (cts.IsCancellationRequested || _playingAudio?.Channel.Id != channel.Id)
        {
            return; // stopped or switched while probing — do not relabel or restart
        }

        var decision = policy.Decide(new PlaybackFailureSignal(reason, HttpStatusCode: status));
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
            return; // stop / switch / close cancelled the wait — never restart the old station
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
        StopAudio();
        await RecordPlayOutcome(channel.Id, false);
        var report = FailureReportFormatter.Format(new FailureReport(
            ProductInfo.Version,
            DateTimeOffset.UtcNow,
            channel.Title,
            channel.Url,
            channel.MediaKind,
            PlaybackErrorClassifier.Classify(reason)));
        var dialog = new PlaybackFailureDialog(channel.Title, channel.SourceOrigin, report) { Owner = this };
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
        if (_suppressAudioVolumeSave)
        {
            return;
        }

        if (_state.AudioVolume == volume)
        {
            return;
        }

        _state = await _store.SaveAsync(_state with { AudioVolume = volume });
    }

    private void StopAudioButton_Click(object sender, RoutedEventArgs e) => StopAudio();

    private void StopAudio()
    {
        StopAudioPlayback();
        _ = StartPreviewsAsync();
    }

    private void StopAudioPlayback() => StopAudioPlayback(clearSystemSession: true);

    // clearSystemSession is false only for the SP-0021 pause path, which stops the live session but
    // keeps the Windows media session visible as Paused so a later system Play can resume the channel.
    private void StopAudioPlayback(bool clearSystemSession)
    {
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
        StopAudioButton.IsEnabled = false;
        AudioVolumeSlider.Visibility = Visibility.Collapsed;
        SetNowPlaying("NothingPlaying");
        if (clearSystemSession)
        {
            _audioPausedChannel = null;
            ClearSystemMediaSession();
        }
    }

    private void OpenIndependentPlayerWindow(StreamChannel channel, bool startFullscreen = false)
    {
        var window = new PlayerWindow(
            channel,
            _log,
            RecordPlayOutcome,
            RemoveChannelAsync,
            channel.Pinned,
            pinned => SetChannelPinnedAsync(channel, pinned),
            _state.PlayerWindowTopmost,
            SavePlayerTopmostAsync,
            _state.VideoVolume,
            _state.VideoMuted,
            SaveVideoAudioPreferencesAsync,
            (url, frame) => _previewCoordinator?.IngestFrame(url, frame),
            _state.VideoBackend,
            startFullscreen) { Owner = this };
        _openPlayerWindows++;
        _ = SuspendPreviewsAsync();
        window.Closed += async (_, _) =>
        {
            _openPlayerWindows = Math.Max(0, _openPlayerWindows - 1);
            await StartPreviewsAsync();
        };
        window.Show();
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

        _state = await _store.SaveAsync(_state);
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

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        AddButton.IsEnabled = !busy;
        SettingsButton.IsEnabled = !busy;
        CatalogProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static bool LanguageContains(string? value, string language) =>
        value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(item => item.Equals(language, StringComparison.OrdinalIgnoreCase)) == true;
}
