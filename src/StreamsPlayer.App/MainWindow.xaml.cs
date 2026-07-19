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
    private ChannelRow? _selectedRow;
    private bool _busy;
    private bool _isGridMode;
    private bool _windowActive = true;
    private int _catalogColumns = 1;
    private CancellationTokenSource? _viewportDebounce;
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
            var memoryCache = new PreviewFrameCache(64, TimeSpan.FromSeconds(60), url =>
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
            var frameStore = new PreviewFrameStore(Path.Combine(_dataDirectory, "grid-previews"), 64, 75);
            var captureService = new VideoFrameCaptureService();
            _previewCoordinator = new GridPreviewCoordinator(
                Dispatcher,
                GetVisibleRows,
                ApplyPreview,
                memoryCache,
                frameStore,
                captureService);
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
            Topmost = _state.MainWindowTopmost;
            MainTopmostCheckBox.IsChecked = _state.MainWindowTopmost;
            LanguageButton.IsEnabled = true;
            MainTopmostCheckBox.IsEnabled = true;
            _preferencesLoaded = true;
            UpdateLocalizedOptions();
            IsGridMode = _state.ViewMode == CatalogViewMode.Grid;
            UpdateViewModeControls();
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
        var channel = new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = url,
            Title = title,
            MediaKind = StreamMediaKindClassifier.Classify(url),
            SourceOrigin = SourceOrigin.Manual,
            SortIndex = nextOrder,
            AddedAt = DateTimeOffset.UtcNow
        };
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

        IEnumerable<StreamChannel> channels = _state.Channels.Where(channel =>
            (query.Length == 0 || Contains(channel.Title, query) || Contains(channel.Topic, query) || Contains(channel.Language, query)) &&
            (category is null or AllValue || string.Equals(channel.Category, category, StringComparison.OrdinalIgnoreCase)) &&
            (language is null or AllValue || LanguageContains(channel.Language, language)) &&
            (country is null or AllValue || string.Equals(channel.Country, country, StringComparison.OrdinalIgnoreCase)) &&
            (media == AllValue || media == "Audio" && channel.MediaKind == MediaKind.Audio ||
             media == "Video" && channel.MediaKind is MediaKind.Video or MediaKind.Rtsp));

        var pinned = channels.Where(channel => channel.Pinned).OrderBy(channel => channel.SortIndex);
        var unpinned = SortUnpinned(channels.Where(channel => !channel.Pinned));
        var atlasPath = _store.ResolveAtlasPath(_state);
        var maximumIndex = _state.Channels
            .Where(channel => channel.SourceOrigin == SourceOrigin.Catalog)
            .Select(channel => channel.FaviconIndex)
            .DefaultIfEmpty(null)
            .Max();
        Rows.Clear();
        foreach (var channel in pinned.Concat(unpinned))
        {
            Rows.Add(GetOrCreateRow(channel, atlasPath, maximumIndex));
        }
        RebuildGridRows();

        EmptyPanel.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StreamsList.Visibility = Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!_busy)
        {
            SetStatus("ChannelCount", Rows.Count, _state.Channels.Count);
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

    private IEnumerable<StreamChannel> SortUnpinned(IEnumerable<StreamChannel> channels) =>
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
        SetFacet(CategoryFilter, _state.Channels.Select(channel => channel.Category));
        SetFacet(LanguageFilter, _state.Channels.SelectMany(channel =>
            channel.Language?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? []));
        SetFacet(CountryFilter, _state.Channels.Select(channel => channel.Country));
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

        var channel = row.Channel with
        {
            Pinned = !row.Channel.Pinned,
            SortIndex = row.Channel.Pinned
                ? row.Channel.SortIndex
                : (_state.Channels.Where(item => item.Pinned).Select(item => item.SortIndex).DefaultIfEmpty(0).Min() - 1)
        };
        ReplaceChannel(channel);
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
            Tag = row,
            IsEnabled = row.Channel.SourceOrigin == SourceOrigin.Manual,
            ToolTip = LocalizationService.Get("MenuEditUnavailable")
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
        if ((sender as MenuItem)?.Tag is not ChannelRow row || row.Channel.SourceOrigin != SourceOrigin.Manual)
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
        ReplaceChannel(row.Channel with
        {
            Url = url,
            Title = title,
            MediaKind = StreamMediaKindClassifier.Classify(url)
        });
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
            MessageBox.Show(this, LocalizationService.Get("OfflinePlayback"), LocalizationService.Get("ProductName"));
            return;
        }

        if (channel.MediaKind == MediaKind.Audio)
        {
            StopAudio();
            _playingAudio = GetOrCreateRow(channel, _store.ResolveAtlasPath(_state), null);
            _playingAudio.SetPlayingAudio(true);
            AudioPlayer.Source = new Uri(channel.Url);
            AudioPlayer.Play();
            StopAudioButton.IsEnabled = true;
            SetNowPlaying("ConnectingAudio", StreamTitleFormatter.Display(channel.Title));
        }
        else
        {
            OpenIndependentPlayerWindow(channel, startFullscreen);
        }
    }

    private async void AudioPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_playingAudio is null)
        {
            return;
        }

        SetNowPlaying("NowPlaying", _playingAudio.DisplayTitle);
        await RecordPlayOutcome(_playingAudio.Channel.Id, true);
    }

    private async void AudioPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        var row = _playingAudio;
        StopAudio();
        if (row is not null)
        {
            await RecordPlayOutcome(row.Channel.Id, false);
        }
        MessageBox.Show(this, e.ErrorException?.Message ?? LocalizationService.Get("AudioFailed"),
            LocalizationService.Get("StreamUnavailableTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void StopAudioButton_Click(object sender, RoutedEventArgs e) => StopAudio();

    private void StopAudio()
    {
        AudioPlayer.Stop();
        AudioPlayer.Source = null;
        _playingAudio?.SetPlayingAudio(false);
        _playingAudio = null;
        StopAudioButton.IsEnabled = false;
        SetNowPlaying("NothingPlaying");
    }

    private void OpenIndependentPlayerWindow(StreamChannel channel, bool startFullscreen = false) =>
        new PlayerWindow(
            channel,
            RecordPlayOutcome,
            _state.PlayerWindowTopmost,
            SavePlayerTopmostAsync,
            _state.VideoVolume,
            _state.VideoMuted,
            SaveVideoAudioPreferencesAsync,
            startFullscreen) { Owner = this }.Show();

    private async Task RecordPlayOutcome(Guid id, bool succeeded)
    {
        var channel = _state.Channels.FirstOrDefault(item => item.Id == id);
        if (channel is null)
        {
            return;
        }

        ReplaceChannel(channel with
        {
            LastPlayOutcome = succeeded ? PlayOutcome.Ok : PlayOutcome.Fail,
            LastPlayOutcomeAt = DateTimeOffset.UtcNow,
            LastPlayedAt = succeeded ? DateTimeOffset.UtcNow : channel.LastPlayedAt
        });
        _state = await _store.SaveAsync(_state);
        ApplyFilter();
    }

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
