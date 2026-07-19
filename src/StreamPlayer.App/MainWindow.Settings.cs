using System.Windows;
using StreamPlayer.Core;

namespace StreamPlayer.App;

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
        var dialog = new SettingsWindow(_state.TileSize, _state.UpdateStreamPreviews, _state.Language, _selectedRow?.Channel)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var tileSizeChanged = dialog.SelectedTileSize != _state.TileSize;
        var previewsChanged = dialog.UpdateStreamPreviews != _state.UpdateStreamPreviews;
        _state = await _store.SaveAsync(_state with
        {
            TileSize = dialog.SelectedTileSize,
            UpdateStreamPreviews = dialog.UpdateStreamPreviews
        });

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
        if (_previewCoordinator is null)
        {
            return;
        }

        if (_state.UpdateStreamPreviews && IsGridMode && _windowActive)
        {
            await StartPreviewsAsync();
        }
        else
        {
            await _previewCoordinator.StopAsync();
        }
    }
}
