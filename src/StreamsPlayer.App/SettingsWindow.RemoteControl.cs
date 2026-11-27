using System.Windows;

namespace StreamsPlayer.App;

public partial class SettingsWindow
{
    private static RemoteCommandTarget? RemoteTarget => ((App)Application.Current).RemoteTarget;

    private void InitializeRemoteControl()
    {
        if (RemoteTarget is not { } target)
        {
            RemoteListeningOption.IsEnabled = false;
            return;
        }

        target.PresentationChanged += RefreshRemoteListening;
        Closed += (_, _) => target.PresentationChanged -= RefreshRemoteListening;
        RemoteListeningOption.IsChecked = target.Settings.Listening;
    }

    private void RefreshRemoteListening()
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(() => RemoteListeningOption.IsChecked = RemoteTarget?.Settings.Listening == true);
        }
    }

    private void RemoteListening_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(RemoteListening_Click), SetRemoteListeningAsync);

    private async Task SetRemoteListeningAsync()
    {
        if (RemoteTarget is not { } target)
        {
            return;
        }

        var listening = RemoteListeningOption.IsChecked;
        RemoteListeningOption.IsEnabled = false;
        var saved = await target.UpdateSettingsAsync(settings => settings with { Listening = listening }, "settings");
        RemoteListeningOption.IsChecked = target.Settings.Listening;
        RemoteListeningOption.IsEnabled = true;
        if (!IsVisible)
        {
            return;
        }

        if (!saved)
        {
            MessageBox.Show(this, LocalizationService.Get("RemoteSaveFailed"), LocalizationService.Get("RemoteControlTitle"));
        }
        else if (listening)
        {
            _mainWindow.RequestExternalControlAfterSettings(this, showControllers: true);
        }
    }

    private void OpenExternalControl_Click(object sender, RoutedEventArgs e) =>
        _mainWindow.RequestExternalControlAfterSettings(this, showControllers: false);

    internal void FocusExchangeSignIn()
    {
        Loaded += (_, _) =>
        {
            ExchangeSecretBox.Focus();
            ExchangeSecretBox.BringIntoView();
        };
    }
}
