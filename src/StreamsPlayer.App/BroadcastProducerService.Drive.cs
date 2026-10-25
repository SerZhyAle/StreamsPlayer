using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed partial class BroadcastProducerService
{
    private Task PublishDriveAsync()
    {
        if (_drive is null)
            return Task.CompletedTask;
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
        return _drive.UpdateBroadcastsAsync(announcements);
    }
}
