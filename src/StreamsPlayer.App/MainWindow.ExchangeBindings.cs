using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private async void ApplyExchangeEntryBindings()
    {
        try
        {
            if (_busy || _stateCommitter is null)
            {
                _exchangeDirectoryPending = true;
                return;
            }

            var entries = Exchange.Guests.Entries.ToList();
            if (Exchange.HasAccount)
            {
                entries.Add(Exchange.Account);
            }

            var identities = entries.Select(entry => ExchangeAccountIdentity.Key(entry.Host, entry.Port, entry.Login)).ToArray();
            await PersistAsync(state =>
            {
                var channels = ExchangeDirectoryChannels.ReconcileEntries(state.Channels, identities);
                return channels.SequenceEqual(state.Channels) ? state : state with { Channels = [.. channels] };
            });
            ApplyFilter();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(ApplyExchangeEntryBindings), exception);
        }
    }

    internal async Task ApplyGuestDirectoryAsync(ExchangeAccount entry, ExchangeDirectorySnapshot snapshot)
    {
        if (_busy || Exchange.Guests.Find(entry.Host, entry.Port, entry.Login) is null)
        {
            return;
        }

        var identity = ExchangeAccountIdentity.Key(entry.Host, entry.Port, entry.Login);
        await PersistAsync(state =>
        {
            var refreshed = ExchangeDirectoryChannels.RefreshStored(state.Channels,
                snapshot.Groups.SelectMany(group => group.Broadcasts), DateTimeOffset.UtcNow, identity);
            var ended = ExchangeDirectoryChannels.MarkEnded(refreshed, snapshot.LiveSourceIds,
                snapshot.LiveBroadcastIds, DateTimeOffset.UtcNow, identity).ToDictionary(channel => channel.Id);
            var channels = refreshed.Select(channel => ended.GetValueOrDefault(channel.Id) ?? channel).ToList();
            return channels.SequenceEqual(state.Channels) ? state : state with { Channels = channels };
        });
        ApplyFilter();
    }
}
