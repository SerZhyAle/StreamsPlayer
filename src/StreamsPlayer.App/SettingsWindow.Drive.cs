using System.Windows;

namespace StreamsPlayer.App;

public partial class SettingsWindow
{
    private DriveSourceService Drive => ((App)Application.Current).Drive;
    private CancellationTokenSource? _driveSignIn;

    private void InitializeDrive()
    {
        Drive.Changed += DriveChanged;
        Closed += (_, _) => { _driveSignIn?.Cancel(); Drive.Changed -= DriveChanged; };
        ShowDrive();
    }

    private void DriveChanged()
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(ShowDrive);
    }

    private void ShowDrive()
    {
        DriveSignInButton.IsEnabled = Drive.Available && !Drive.SignedIn && _driveSignIn is null;
        DriveSignOutButton.IsEnabled = Drive.SignedIn && _driveSignIn is null;
        DriveCancelButton.Visibility = _driveSignIn is null ? Visibility.Collapsed : Visibility.Visible;
        DriveStatusText.Text = LocalizationService.Get(Drive.Available ? Drive.StatusKey : "DriveUnavailable");
    }

    private void DriveSignIn_Click(object sender, RoutedEventArgs e) => HandlerBoundary.Run(nameof(DriveSignIn_Click), async () =>
    {
        if (_driveSignIn is not null) return;
        using var cancellation = new CancellationTokenSource();
        _driveSignIn = cancellation;
        try { ShowDrive(); await Drive.SignInAsync(cancellation.Token); }
        finally { _driveSignIn = null; ShowDrive(); }
    });

    private void DriveSignOut_Click(object sender, RoutedEventArgs e) => HandlerBoundary.Run(nameof(DriveSignOut_Click), async () =>
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        DriveSignOutButton.IsEnabled = false;
        try { await Drive.SignOutAsync(cancellation.Token); }
        finally { ShowDrive(); }
    });

    private void DriveCancel_Click(object sender, RoutedEventArgs e) => _driveSignIn?.Cancel();
}
