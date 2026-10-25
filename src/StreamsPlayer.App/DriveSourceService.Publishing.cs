using System.Diagnostics;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed partial class DriveSourceService
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _publicationClock = Stopwatch.StartNew();
    private readonly object _outgoingGate = new();
    private readonly Task _publicationTask;
    private IReadOnlyList<DriveAnnouncement> _outgoing = [];
    private bool _publicationRequested;
    private readonly DrivePollDelay _publicationDelay = new();
    private TimeSpan _publishAfter;
    private bool _publicationFailed;

    internal async Task UpdateBroadcastsAsync(IReadOnlyList<DriveAnnouncement> broadcasts)
    {
        lock (_outgoingGate) { _outgoing = broadcasts; _publicationRequested = true; }
        if (!SignedIn) return;
        await PublishCurrentAsync(_lifetime.Token).ConfigureAwait(false);
    }

    private async Task PublishCurrentAsync(CancellationToken cancellation, bool scheduled = false)
    {
        await _operations.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (scheduled && _publicationClock.Elapsed < _publishAfter) return;
            IReadOnlyList<DriveAnnouncement> outgoing;
            lock (_outgoingGate) outgoing = _outgoing;
            if (_account is not null && _publisher is not null)
                await _publisher.SynchronizeAsync(_account.DeviceId, outgoing, DateTimeOffset.UtcNow,
                    _publicationClock.Elapsed, cancellation).ConfigureAwait(false);
            _publicationDelay.After(true, 0);
            _publishAfter = TimeSpan.Zero;
            if (_publicationFailed)
            {
                _publicationFailed = false;
                if (StatusKey == "DrivePublishFailed") StatusKey = "DriveConnected";
                Changed?.Invoke();
            }
        }
        catch (Exception exception) when (IsFailure(exception) && !cancellation.IsCancellationRequested)
        {
            if (exception is DriveRequestException { StatusCode: 400 or 401 })
                ForgetFailedGrant();
            else { _publicationFailed = true; StatusKey = "DrivePublishFailed"; }
            _publishAfter = _publicationClock.Elapsed + _publicationDelay.After(false, Random.Shared.NextDouble());
            Changed?.Invoke();
        }
        finally { _operations.Release(); }
    }

    private async Task RunPublicationAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                bool requested;
                lock (_outgoingGate) requested = _publicationRequested;
                if (requested)
                    await PublishCurrentAsync(_lifetime.Token, scheduled: true).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    internal async Task StopAsync()
    {
        _lifetime.Cancel();
        await _publicationTask.ConfigureAwait(false);
    }
}
