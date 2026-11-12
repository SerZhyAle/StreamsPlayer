using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed partial class BroadcastProducerService
{
    private async Task PublishDriveAsync()
    {
        if (_drive is null)
            return;
        List<DriveAnnouncement> announcements = [];
        lock (_gate)
        {
            foreach (var state in _running.Values)
            {
                if (OutwardDescriptor(state) is not { } descriptor)
                    continue;
                announcements.Add(new DriveAnnouncement(state.BroadcastId, new
                {
                    broadcastId = state.BroadcastId, deviceId = _drive.DeviceId ?? _exchange.Account.DeviceId,
                    title = state.Title, mode = state.Plan?.Mode ?? BroadcastSources.Mode(state.Source),
                    startedAt = state.StartedAt, updatedAt = state.StartedAt,
                    descriptor = JsonSerializer.Deserialize<JsonElement>(descriptor)
                }));
            }
        }
        if (!_drive.SignedIn) return;
        await _drive.UpdateBroadcastsAsync(announcements).ConfigureAwait(false);
        _diagnostic("BROADCAST | step=drive-publication | records=" + announcements.Count
            + " | code=" + _drive.StatusKey);
    }
}
