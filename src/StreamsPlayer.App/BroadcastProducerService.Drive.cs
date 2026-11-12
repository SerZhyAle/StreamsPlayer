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
        // S4-1: the outgoing list is stored even while signed out, so the first publication after sign-in carries
        // today's broadcasts instead of the list of the last start or stop (UpdateBroadcastsAsync publishes only
        // when signed in). Only the diagnostic line below waits for a sign-in.
        await _drive.UpdateBroadcastsAsync(announcements).ConfigureAwait(false);
        if (!_drive.SignedIn) return;
        _diagnostic("BROADCAST | step=drive-publication | records=" + announcements.Count
            + " | code=" + _drive.StatusKey);
    }
}
