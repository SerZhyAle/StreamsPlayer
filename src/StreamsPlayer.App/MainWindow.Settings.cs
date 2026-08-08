using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    public double GridTileWidth => _state.TileSize switch
    {
        StreamTileSize.VerySmall => 120,
        StreamTileSize.Small => 240,
        StreamTileSize.Large => 400,
        _ => 320
    };

    public double GridTileHeight => GridTileWidth * 9 / 16;
    public bool IsVerySmallTile => _state.TileSize == StreamTileSize.VerySmall;

    /// <summary>
    /// Deliberately not gated on <c>_preferencesLoaded</c> (SP-0050). The interface language moved in
    /// here from the header, and it has to stay reachable when the catalog load fails: on a failed load
    /// <see cref="PersistAsync"/> discards the write, but <c>LocalizationService.Apply</c> still runs, so
    /// the language changes for the session. That was the header button's behaviour and the ticket's
    /// first constraint requires keeping it.
    /// </summary>
    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_state.Theme, _state.TileSize, _state.UpdateStreamPreviews, _state.KeepAwakeDuringPlayback, _state.SystemMediaControls, _state.ResumePlaybackOnStartup, _state.VideoBackend, _state.FrameFolder, LocalizationService.CurrentLanguage, _selectedRow?.Channel, RunSettingsActionAsync)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var tileSizeChanged = dialog.SelectedTileSize != _state.TileSize;
        var previewsChanged = dialog.UpdateStreamPreviews != _state.UpdateStreamPreviews;
        var systemMediaControlsChanged = dialog.SystemMediaControls != _state.SystemMediaControls;
        // SP-0062: the launch already gates on the preference, so clearing is not what makes the switch
        // work - it is that a list of what the user was listening to should not outlive their decision to
        // stop the feature from using it.
        var resumeTurnedOff = _state.ResumePlaybackOnStartup && !dialog.ResumePlaybackOnStartup;
        // Null means "the language already in use", so the settings save stays a single write.
        var chosenLanguage = dialog.SelectedLanguage;
        _state = await PersistAsync(_state with
        {
            Language = chosenLanguage ?? _state.Language,
            Theme = dialog.SelectedTheme,
            TileSize = dialog.SelectedTileSize,
            UpdateStreamPreviews = dialog.UpdateStreamPreviews,
            KeepAwakeDuringPlayback = dialog.KeepAwakeDuringPlayback,
            SystemMediaControls = dialog.SystemMediaControls,
            // Read at launch, so this needs no side effect applied below - it takes effect next time.
            ResumePlaybackOnStartup = dialog.ResumePlaybackOnStartup,
            ResumeChannelIds = resumeTurnedOff ? [] : _state.ResumeChannelIds,
            // Takes effect on the next player window opened; an already-open player keeps its engine.
            VideoBackend = dialog.SelectedVideoBackend,
            // Read per capture, so an open player window picks this up without being reopened (SP-0038).
            FrameFolder = dialog.FrameFolder
        });

        ThemeService.Apply(_state.Theme);

        if (chosenLanguage is { } language)
        {
            LocalizationService.Apply(language);
            RefreshLocalizedInterface();
        }

        // Toggling off releases an active wake lock immediately; toggling on re-acquires it for any
        // session already playing (the guard recomputes from its live request counts).
        WakeGuard.Enabled = _state.KeepAwakeDuringPlayback;

        if (systemMediaControlsChanged)
        {
            ApplySystemMediaControlsSetting();
        }

        if (tileSizeChanged)
        {
            PropertyChanged?.Invoke(this, new(nameof(GridTileWidth)));
            PropertyChanged?.Invoke(this, new(nameof(GridTileHeight)));
            PropertyChanged?.Invoke(this, new(nameof(IsVerySmallTile)));
            _catalogColumns = 0;
            UpdateCatalogColumns();
        }

        if (previewsChanged)
        {
            await ApplyPreviewPreferenceAsync();
        }
        UpdateViewModeControls();
        SetStatus("SettingsApplied");
    }

    private async Task ApplyPreviewPreferenceAsync()
    {
        // The coordinator keeps running to show stored thumbnails; the toggle only changes whether blanks are captured.
        if (_previewCoordinator is null || !IsGridMode || !_windowActive)
        {
            return;
        }

        await StartPreviewsAsync();
        await QueueVisibleSafelyAsync(force: false);
    }
}
