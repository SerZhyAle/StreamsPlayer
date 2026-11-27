using System.ComponentModel;
using System.IO;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private readonly SettingsUiStateStore _externalControlState = new(Path.Combine(AppPaths.DataDirectory, "external-control"));
    private ExternalControlWindow? _externalControlWindow;
    private Task _externalControlWrite = Task.CompletedTask;
    private bool _openingExternalControl;
    private bool _showExternalControllers;
    private bool? _externalControlAfterSettings;

    internal Task ExternalControlPlacementWrite => _externalControlWrite;

    internal void RequestExternalControlAfterSettings(Window settings, bool showControllers)
    {
        _externalControlAfterSettings = showControllers;
        settings.Close();
        if (settings.IsVisible)
        {
            _externalControlAfterSettings = null;
        }
    }

    private async Task OpenExternalControlAsync(bool showControllers)
    {
        if (_externalControlWindow is { } existing)
        {
            existing.BringForward(showControllers);
            return;
        }

        _showExternalControllers |= showControllers;
        if (_openingExternalControl || ((App)Application.Current).RemoteTarget is not { } target)
        {
            return;
        }

        _openingExternalControl = true;
        try
        {
            await _externalControlWrite;
            var state = await _externalControlState.LoadAsync();
            if (!IsVisible || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            var window = new ExternalControlWindow(this, target, state.Window);
            _externalControlWindow = window;
            void SavePlacement(object? sender, CancelEventArgs e)
            {
                if (!e.Cancel && window.CurrentPlacement() is { } placement)
                {
                    _externalControlWrite = SaveExternalControlPlacementAsync(_externalControlWrite, state with { Window = placement });
                }
            }

            Closing += SavePlacement;
            window.Closing += SavePlacement;
            window.Closed += (_, _) =>
            {
                Closing -= SavePlacement;
                _externalControlWindow = null;
            };
            window.Show();
            window.BringForward(_showExternalControllers);
        }
        finally
        {
            _openingExternalControl = _showExternalControllers = false;
        }
    }

    private async Task SaveExternalControlPlacementAsync(Task precedingWrite, SettingsUiState state)
    {
        await precedingWrite.ConfigureAwait(false);
        await _externalControlState.SaveAsync(state).ConfigureAwait(false);
    }
}
