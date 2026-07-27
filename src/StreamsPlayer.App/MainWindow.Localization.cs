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

        return await _store.SaveAsync(updated);
    }

    // SP-0029: the toolbar button opens the language menu instead of flipping between two locales.
    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_preferencesLoaded || LanguageButton.ContextMenu is not { } menu)
        {
            return;
        }

        BuildLanguageMenu(menu);
        menu.PlacementTarget = LanguageButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void BuildLanguageMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        foreach (var entry in InterfaceLanguages.All)
        {
            var language = entry.Language;
            var item = new MenuItem
            {
                Header = LocalizationService.NativeName(language),
                IsCheckable = true,
                IsChecked = language == LocalizationService.CurrentLanguage,
                Tag = language
            };
            item.Click += LanguageMenuItem_Click;
            menu.Items.Add(item);
        }
    }

    private async void LanguageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AppLanguage language } || language == LocalizationService.CurrentLanguage)
        {
            // Re-checking the active language must not rewrite state or flicker the interface.
            UpdateLanguageButton();
            return;
        }

        LocalizationService.Apply(language);
        _state = await PersistAsync(_state with { Language = language });
        RefreshLocalizedInterface();
    }

    // SP-0034: the badge is derived from the registry rather than from a second per-language switch.
    // Phase 07 replaces it with a glyph, at which point no layout width depends on a language code.
    // Reads the applied language rather than the stored one: on a fresh install the stored value is
    // still null while the interface is already showing the detected language.
    private void UpdateLanguageButton() =>
        LanguageButton.Content = InterfaceLanguages.For(LocalizationService.CurrentLanguage)
            .DictionaryCode.ToUpperInvariant();

    private async void MainTopmostCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_preferencesLoaded)
        {
            return;
        }

        var topmost = MainTopmostCheckBox.IsChecked == true;
        Topmost = topmost;
        _state = await PersistAsync(_state with { MainWindowTopmost = topmost });
    }

    private async Task SavePlayerTopmostAsync(bool topmost)
    {
        if (_state.PlayerWindowTopmost == topmost)
        {
            return;
        }

        _state = await PersistAsync(_state with { PlayerWindowTopmost = topmost });
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
        UpdateLanguageButton();
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
    }

    private void SetNowPlaying(string resourceKey, params object?[] arguments)
    {
        _nowPlayingResourceKey = resourceKey;
        _nowPlayingArguments = arguments;
        NowPlayingText.Text = LocalizationService.Format(resourceKey, arguments);
        RefreshWindowTitle();
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
    }
}
