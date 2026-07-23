using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    public double GridTileWidth => _state.TileSize switch
    {
        StreamTileSize.Small => 240,
        StreamTileSize.Large => 400,
        _ => 320
    };

    public double GridTileHeight => GridTileWidth * 9 / 16;

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_state.TileSize, _state.UpdateStreamPreviews, _state.KeepAwakeDuringPlayback, _state.SystemMediaControls, _state.VideoBackend, _state.Language, _selectedRow?.Channel, RunStreamListPortabilityAsync)
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
        _state = await _store.SaveAsync(_state with
        {
            TileSize = dialog.SelectedTileSize,
            UpdateStreamPreviews = dialog.UpdateStreamPreviews,
            KeepAwakeDuringPlayback = dialog.KeepAwakeDuringPlayback,
            SystemMediaControls = dialog.SystemMediaControls,
            // Takes effect on the next player window opened; an already-open player keeps its engine.
            VideoBackend = dialog.SelectedVideoBackend
        });

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
