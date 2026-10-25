using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private (ExchangeDirectorySnapshot Snapshot, string Identity)? _pendingDriveDirectory;

    private Task ApplyPendingDriveDirectoryAsync()
    {
        var pending = _pendingDriveDirectory;
        _pendingDriveDirectory = null;
        return pending is { } latest ? ApplyDriveDirectoryAsync(latest.Snapshot, latest.Identity) : Task.CompletedTask;
    }
    internal Task<BroadcastOpenResult> OpenDriveBroadcastWindowAsync(ExchangeBroadcastView view) =>
        OpenIncomingBroadcastAsync(() => ImportDriveBroadcastAsync(view), view.Record.Credentials, openedByLink: false);

    internal async Task KeepDriveBroadcastAsync(ExchangeBroadcastView view)
    {
        if (await ImportDriveBroadcastAsync(view) is { } channel)
            await RevealChannelAsync(channel.Id);
    }

    private async Task<StreamChannel?> ImportDriveBroadcastAsync(ExchangeBroadcastView view)
    {
        var drive = ((App)Application.Current).Drive;
        if (!drive.SignedIn || view.Support != ExchangeBroadcastSupport.Supported || view.Record.Descriptor is not { } descriptor)
            return null;
        if (_busy)
        {
            // S12-4: said, as the Exchange and Link imports say it, not a silent null.
            SetStatus("FmsBroadcastBusy");
            return null;
        }
        RememberLinkKeys(descriptor);
        var identity = drive.EntryIdentity;
        FastMediaSorterBroadcastApplyResult? applied = null;
        await PersistAsync(state =>
        {
            applied = FastMediaSorterBroadcastImport.Apply(state.Channels, descriptor, DateTimeOffset.UtcNow,
                view.Record.BroadcastId, directoryEntryIdentity: identity);
            return state with { Channels = [.. applied.Channels] };
        });
        PopulateFacets();
        ApplyFilter();
        return applied?.Channel;
    }

    internal async Task ApplyDriveDirectoryAsync(ExchangeDirectorySnapshot snapshot, string identity)
    {
        if (_busy)
        {
            _pendingDriveDirectory = (snapshot, identity);
            return;
        }
        var changed = false;
        await PersistAsync(state =>
        {
            var ownEntry = Exchange.HasAccount
                ? ExchangeAccountIdentity.Key(Exchange.Account.Host, Exchange.Account.Port, Exchange.Account.Login) : null;
            var managed = state.Channels.Where(channel => channel.FastMediaSorterBroadcast?.DirectoryEntryIdentity is { } bound
                && (bound == identity || bound == ownEntry)).ToList();
            var server = Exchange.Directory;
            var driveViews = snapshot.Groups.SelectMany(group => group.Broadcasts).Where(view =>
                server?.LiveBroadcastIds.Contains(view.Record.BroadcastId) != true
                && (view.Record.Descriptor?.SourceId is not { } id || server?.LiveSourceIds.Contains(id) != true)).ToList();
            foreach (var view in driveViews)
            {
                if (view.Record.Descriptor is { SourceId: { } sourceId } descriptor
                    && managed.Any(channel => channel.FastMediaSorterBroadcast?.SourceId == sourceId))
                    RememberLinkKeys(descriptor);
            }
            var refreshed = ExchangeDirectoryChannels.RefreshStored(managed,
                driveViews, DateTimeOffset.UtcNow, identity);
            var knownLive = DriveDirectory.Merge(server, snapshot)!;
            var ended = ExchangeDirectoryChannels.MarkEnded(refreshed, knownLive.LiveSourceIds,
                knownLive.LiveBroadcastIds, DateTimeOffset.UtcNow).ToDictionary(channel => channel.Id);
            var updated = refreshed.Select(channel => ended.GetValueOrDefault(channel.Id, channel)).ToDictionary(channel => channel.Id);
            var channels = state.Channels.Select(channel => updated.GetValueOrDefault(channel.Id, channel)).ToList();
            if (channels.SequenceEqual(state.Channels)) return state;
            changed = true;
            return state with { Channels = channels };
        });
        if (changed) { PopulateFacets(); ApplyFilter(); }
    }
}
