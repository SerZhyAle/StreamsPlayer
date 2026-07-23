using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var rows = _state.ListeningHistory
            .Select(entry =>
            {
                var live = _state.Channels.FirstOrDefault(channel => channel.Id == entry.ChannelId);
                var title = StreamTitleFormatter.Display(live?.Title ?? entry.Title);
                var playedAt = LocalizationService.Format("HistoryPlayedAt", entry.LastPlayedAt.ToLocalTime());
                return new HistoryRowView(entry.ChannelId, title, playedAt, entry.LastTrackText, live is not null);
            })
            .ToList();

        var window = new ListeningHistoryWindow(rows, PlayFromHistoryAsync, ClearHistoryAsync) { Owner = this };
        window.ShowDialog();
    }

    // Play a history entry by resolving its id against the current catalog. A removed channel is never
    // reopened from a stale address (SP-0019) — it just reports unavailable and nothing is played.
    private async Task PlayFromHistoryAsync(Guid channelId)
    {
        var channel = _state.Channels.FirstOrDefault(item => item.Id == channelId);
        if (channel is null)
        {
            SetStatus("HistoryUnavailable");
            return;
        }

        await PlayChannelAsync(channel, rememberSelection: true);
    }

    // Clear all history. Touches only ListeningHistory — channels, pins, collections, play marks,
    // the hidden set, and catalog data are untouched (AC5).
    private async Task ClearHistoryAsync()
    {
        _state = await _store.SaveAsync(_state with { ListeningHistory = [] });
    }
}
