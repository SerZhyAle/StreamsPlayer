using System.Windows;
using System.Windows.Controls;
using StreamPlayer.Core;

namespace StreamPlayer.App;

public partial class MainWindow
{
    private bool _preferencesLoaded;
    private bool _updatingLocalizedOptions;
    private string _statusResourceKey = "Ready";
    private object?[] _statusArguments = [];
    private string _nowPlayingResourceKey = "NothingPlaying";
    private object?[] _nowPlayingArguments = [];

    private async void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_preferencesLoaded)
        {
            return;
        }

        var language = _state.Language == AppLanguage.English ? AppLanguage.Russian : AppLanguage.English;
        LocalizationService.Apply(language);
        _state = await _store.SaveAsync(_state with { Language = language });
        RefreshLocalizedInterface();
    }

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
        UpdateLocalizedOptions();
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
        _updatingLocalizedOptions = true;
        try
        {
            var mediaItems = new[]
            {
                new UiOption(AllValue, LocalizationService.Get("AllOption")),
                new UiOption("Audio", LocalizationService.Get("AudioOption")),
                new UiOption("Video", LocalizationService.Get("VideoOption"))
            };
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
    }

    private void RefreshLocalizedStateText()
    {
        StatusText.Text = LocalizationService.Format(_statusResourceKey, _statusArguments);
        NowPlayingText.Text = LocalizationService.Format(_nowPlayingResourceKey, _nowPlayingArguments);
    }
}
