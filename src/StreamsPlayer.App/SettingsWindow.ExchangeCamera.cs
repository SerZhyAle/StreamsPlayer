using System.Windows;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0229: the camera door of the first-connection prompt. The control is offered only where Windows reports a
/// camera and is absent - not disabled - everywhere else. Asking lights no camera and needs no permission; the
/// camera is opened only by the scan window, only after the press.
/// </summary>
public partial class SettingsWindow
{
    private IReadOnlyList<ExchangeCameraInfo> _exchangeCameras = [];
    private bool _probingCameras;

    private void InitializeExchangeCamera()
    {
        // Once when the window opens and again whenever it regains focus, so a camera plugged in meanwhile appears.
        Loaded += (_, _) => HandlerBoundary.Run(nameof(ProbeExchangeCamerasAsync), ProbeExchangeCamerasAsync);
        Activated += (_, _) => HandlerBoundary.Run(nameof(ProbeExchangeCamerasAsync), ProbeExchangeCamerasAsync);
    }

    private async Task ProbeExchangeCamerasAsync()
    {
        if (_probingCameras || _closed)
        {
            return;
        }

        _probingCameras = true;
        try
        {
            var found = await ExchangeCameraCatalog.FindAsync();
            if (_closed)
            {
                return;
            }

            _exchangeCameras = found;
            ShowExchangeCameraDoor();
        }
        finally
        {
            _probingCameras = false;
        }
    }

    private void ShowExchangeCameraDoor()
    {
        ExchangeImportCameraButton.Visibility = _exchangeCameras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExchangeImportCameraButton.IsEnabled = !_exchangeBusy;
    }

    private void ExchangeImportCamera_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(ExchangeImportCamera_Click), () => RunExchangeActionAsync(() =>
        {
            var window = new ExchangeCameraScanWindow(_exchangeCameras) { Owner = this };
            if (window.ShowDialog() == true && window.Result is { } result)
            {
                // The same prompt every other door fills; nothing connects until the person presses the sign-in.
                ApplyExchangeImport(result);
            }

            return Task.CompletedTask;
        }));
}
