using System.Windows;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class BroadcastsWindow
{
    private DriveSourceService Drive => ((App)Application.Current).Drive;
    private readonly DispatcherTimer _driveTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource? _drivePoll;
    private bool _drivePolling;
    private bool _driveClosed;
    private readonly DrivePollDelay _driveDelay = new();

    private void InitializeDriveView()
    {
        Drive.Changed += DriveChanged;
        _driveTimer.Tick += (_, _) => HandlerBoundary.Run(nameof(PollDriveAsync), PollDriveAsync);
        Loaded += (_, _) => { _driveTimer.Start(); HandlerBoundary.Run(nameof(PollDriveAsync), PollDriveAsync); };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) { _driveTimer.Stop(); _drivePoll?.Cancel(); }
            else { _driveTimer.Start(); HandlerBoundary.Run(nameof(PollDriveAsync), PollDriveAsync); }
        };
        Closed += (_, _) =>
        {
            _driveClosed = true;
            _driveTimer.Stop();
            _drivePoll?.Cancel();
            Drive.Changed -= DriveChanged;
        };
    }

    private bool IsDriveView(ExchangeBroadcastView view) => Drive.Directory?.Groups
        .SelectMany(group => group.Broadcasts).Any(item => ReferenceEquals(item, view)) == true;

    private bool IsDriveDevice(string deviceId) => Drive.Directory?.Devices?.Any(device => device.DeviceId == deviceId) == true
        && _source.Directory?.Devices?.Any(device => device.DeviceId == deviceId) != true;

    private void DriveChanged()
    {
        if (!_driveClosed && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(() => { if (!_driveClosed) Rebuild(); });
    }

    private async Task PollDriveAsync()
    {
        if (_driveClosed || !IsVisible || WindowState == WindowState.Minimized || !Drive.SignedIn || _drivePolling)
            return;
        _drivePolling = true;
        using var cancellation = new CancellationTokenSource();
        _drivePoll = cancellation;
        try
        {
            var succeeded = await Drive.PollAsync(cancellation.Token);
            if (!cancellation.IsCancellationRequested && Drive.Directory is { } snapshot)
                await _host.ApplyDriveDirectoryAsync(snapshot, Drive.EntryIdentity);
            _driveTimer.Interval = _driveDelay.After(succeeded, Random.Shared.NextDouble());
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { HandlerBoundary.Report(nameof(PollDriveAsync), exception); }
        finally { _drivePoll = null; _drivePolling = false; }
    }
}
