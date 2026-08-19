using System.IO;
using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private bool _preferencesLoaded;
    private bool _updatingLocalizedOptions;

    private string _statusResourceKey = "Ready";
    private object?[] _statusArguments = [];
    private string _nowPlayingResourceKey = "NothingPlaying";
    private object?[] _nowPlayingArguments = [];

    /// <summary>
    /// The only way this window writes state.
    /// <para>
    /// SP-0034: when a load fails, <c>_state</c> is left at its empty field initialiser. Committing
    /// that would replace the user's real catalog, collections, history and pins with nothing, and
    /// <c>StreamCatalogStore</c> would then delete the favicon atlas the empty state does not name.
    /// Most save paths already checked <c>_preferencesLoaded</c>; the ones that did not - volume,
    /// add stream, catalog refresh, import, history - turned a failed load into permanent data loss.
    /// Routing every save through here makes the guard structural instead of remembered.
    /// </para>
    /// </summary>
    private async Task<CatalogState> PersistAsync(CatalogState updated)
    {
        if (!_preferencesLoaded)
        {
            return _state;
        }

        try
        {
            return await _store.SaveAsync(updated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked, full, or redirected state folder is an environment failure, not a defect: an
            // on-access scanner or the MSIX write redirector can hold the temp file the atomic save
            // moves into place. Every caller is an `async void` event handler, so letting it escape
            // reaches DispatcherUnhandledException, which logs but does not handle - the process dies
            // and the window vanishes. Losing one preference write is the honest cost; keep the
            // previous state so the next save still starts from what the user actually has.
            _log.Error("Catalog state save failed", exception);
            return _state;
        }
    }

    private async Task SetPlayerTopmostAsync(bool topmost)
    {
        foreach (var window in _playerWindows)
        {
            window.ApplyPlayerTopmost(topmost);
        }

        if (_state.PlayerWindowTopmost != topmost)
        {
            _state = await PersistAsync(_state with { PlayerWindowTopmost = topmost });
        }
    }

    private async Task SaveVideoAudioPreferencesAsync(int volume, bool muted)
    {
        if (_state.VideoVolume == volume && _state.VideoMuted == muted)
        {
            return;
        }

        _state = await PersistAsync(_state with { VideoVolume = volume, VideoMuted = muted });
    }

    private void RefreshLocalizedInterface()
    {
        // Most of the header needs no update here: content, tooltips and automation names are all
        // DynamicResource bindings and follow the dictionary swap on their own (SP-0034). The operations
        // menu is rebuilt on every open, so it picks the new language up for free.
        UpdateLocalizedOptions();
        // The collection list carries a localized "All" entry, so it is rebuilt with the rest (SP-0017).
        PopulateCollectionFilter();
        PopulateFacets();
        foreach (var row in _rowCache.Values)
        {
            row.RefreshLocalization();
        }

        // Player windows are non-modal, so one can be open while the language changes. Its title and
        // its formatted wait label have no DynamicResource to follow and must be re-rendered here.
        foreach (var player in Application.Current.Windows.OfType<PlayerWindow>())
        {
            player.RefreshLocalization();
        }

        ApplyFilter();
        // The reveal button's tooltip is set in code, not bound, so it has to be re-rendered by hand.
        UpdateFilterPanelChrome();
        RefreshLocalizedStateText();
    }

    private void UpdateLocalizedOptions()
    {
        var selectedMedia = SelectedOptionValue(MediaFilter) ?? AllValue;
        var selectedSort = SelectedOptionValue(SortMode) ?? "Name";
        var selectedMinBitrate = SelectedOptionValue(MinBitrateFilter) ?? AllValue;
        _updatingLocalizedOptions = true;
        try
        {
            var mediaItems = new[]
            {
                new UiOption(AllValue, LocalizationService.Get("AllOption")),
                new UiOption("Audio", LocalizationService.Get("AudioOption")),
                new UiOption("Video", LocalizationService.Get("VideoOption"))
            };
            var minBitrateItems = new[] { new UiOption(AllValue, LocalizationService.Get("AllOption")) }
                .Concat(new[] { 64, 128, 192, 256, 320 }
                    .Select(kbps => new UiOption(
                        kbps.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        LocalizationService.Format("BitrateValue", kbps))))
                .ToArray();
            var sortItems = new[]
            {
                new UiOption("Name", LocalizationService.Get("SortName")),
                new UiOption("Topic", LocalizationService.Get("SortTopic")),
                new UiOption("Language", LocalizationService.Get("SortLanguage")),
                new UiOption("Country", LocalizationService.Get("SortCountry")),
                new UiOption("Recently added", LocalizationService.Get("SortRecentlyAdded"))
            };
            MediaFilter.ItemsSource = mediaItems;
            MediaFilter.SelectedItem = mediaItems.First(item => item.Value == selectedMedia);
            SortMode.ItemsSource = sortItems;
            SortMode.SelectedItem = sortItems.First(item => item.Value == selectedSort);
            MinBitrateFilter.ItemsSource = minBitrateItems;
            MinBitrateFilter.SelectedItem = minBitrateItems.FirstOrDefault(item => item.Value == selectedMinBitrate)
                ?? minBitrateItems[0];
        }
        finally
        {
            _updatingLocalizedOptions = false;
        }
    }

    private static string? SelectedOptionValue(ComboBox comboBox) =>
        (comboBox.SelectedItem as UiOption)?.Value;

    private void SetStatus(string resourceKey, params object?[] arguments)
    {
        _statusResourceKey = resourceKey;
        _statusArguments = arguments;
        StatusText.Text = LocalizationService.Format(resourceKey, arguments);
        UpdateCompactPanel();
    }

    private void SetNowPlaying(string resourceKey, params object?[] arguments)
    {
        _nowPlayingResourceKey = resourceKey;
        _nowPlayingArguments = arguments;
        NowPlayingText.Text = LocalizationService.Format(resourceKey, arguments);
        RefreshWindowTitle();
        UpdateCompactPanel();
    }

    // The title bar (and therefore the taskbar button) names the station currently playing or paused,
    // so a minimised window still says what is on air. Every playback transition goes through
    // SetNowPlaying, so that is the single hook; the product name alone is the idle title.
    private void RefreshWindowTitle()
    {
        var product = LocalizationService.Get("ProductName");
        var station = _playingAudio?.DisplayTitle
            ?? (_audioPausedChannel is { } paused ? StreamTitleFormatter.Display(paused.Title) : null);
        Title = string.IsNullOrWhiteSpace(station)
            ? product
            : LocalizationService.Format("WindowTitleWithSubject", station, product);
    }

    private void RefreshLocalizedStateText()
    {
        StatusText.Text = LocalizationService.Format(_statusResourceKey, _statusArguments);
        NowPlayingText.Text = LocalizationService.Format(_nowPlayingResourceKey, _nowPlayingArguments);
        RefreshWindowTitle();
        // SP-0080: the panel is a copy of these two lines, so a language change reaches it here and
        // nowhere else. Its own captions are DynamicResource and follow the switch on their own.
        UpdateCompactPanel();
    }
}
