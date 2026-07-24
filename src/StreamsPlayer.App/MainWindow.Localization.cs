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
        foreach (var language in LocalizationService.Available)
        {
            var item = new MenuItem
            {
                Header = LocalizationService.NativeName(language),
                IsCheckable = true,
                IsChecked = language == _state.Language,
                Tag = language
            };
            item.Click += LanguageMenuItem_Click;
            menu.Items.Add(item);
        }
    }

    private async void LanguageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AppLanguage language } || language == _state.Language)
        {
            // Re-checking the active language must not rewrite state or flicker the interface.
            UpdateLanguageButton();
            return;
        }

        LocalizationService.Apply(language);
        _state = await _store.SaveAsync(_state with { Language = language });
        RefreshLocalizedInterface();
    }

    private void UpdateLanguageButton() => LanguageButton.Content = LocalizationService.ShortCode(_state.Language);

    private async void MainTopmostCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_preferencesLoaded)
        {
            return;
        }

        var topmost = MainTopmostCheckBox.IsChecked == true;
        Topmost = topmost;
        _state = await _store.SaveAsync(_state with { MainWindowTopmost = topmost });
    }

    private async Task SavePlayerTopmostAsync(bool topmost)
    {
        if (_state.PlayerWindowTopmost == topmost)
        {
            return;
        }

        _state = await _store.SaveAsync(_state with { PlayerWindowTopmost = topmost });
    }

    private async Task SaveVideoAudioPreferencesAsync(int volume, bool muted)
    {
        if (_state.VideoVolume == volume && _state.VideoMuted == muted)
        {
            return;
        }

        _state = await _store.SaveAsync(_state with { VideoVolume = volume, VideoMuted = muted });
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
                        string.Format(LocalizationService.Get("BitrateValue"), kbps))))
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
        Title = string.IsNullOrWhiteSpace(station) ? product : $"{station} - {product}";
    }

    private void RefreshLocalizedStateText()
    {
        StatusText.Text = LocalizationService.Format(_statusResourceKey, _statusArguments);
        NowPlayingText.Text = LocalizationService.Format(_nowPlayingResourceKey, _nowPlayingArguments);
        RefreshWindowTitle();
    }
}
