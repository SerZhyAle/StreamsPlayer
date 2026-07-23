using System.Collections.ObjectModel;
using System.Windows;

namespace StreamsPlayer.App;

// A single Recently-played row. Title/PlayedAt/Track are already display-formatted by the caller.
// Playable is false when the channel id no longer resolves in the catalog, so the row is a dimmed,
// non-playable label (SP-0019): it is never reopened from a stored address.
internal sealed record HistoryRowView(Guid ChannelId, string Title, string PlayedAt, string? Track, bool Playable)
{
    public string TrackText => Track ?? string.Empty;
    public Visibility TrackVisibility =>
        string.IsNullOrWhiteSpace(Track) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UnavailableVisibility => Playable ? Visibility.Collapsed : Visibility.Visible;
    public double RowOpacity => Playable ? 1.0 : 0.55;
}

// SP-0019: shows the local, bounded listening history. Play resolves the channel id against the current
// catalog (a removed channel stays a non-playable label); Clear empties the whole history after confirm.
public partial class ListeningHistoryWindow : Window
{
    private readonly Func<Guid, Task> _play;
    private readonly Func<Task> _clear;
    private readonly ObservableCollection<HistoryRowView> _rows;

    internal ListeningHistoryWindow(IReadOnlyList<HistoryRowView> rows, Func<Guid, Task> play, Func<Task> clear)
    {
        InitializeComponent();
        _play = play;
        _clear = clear;
        _rows = new ObservableCollection<HistoryRowView>(rows);
        HistoryList.ItemsSource = _rows;
        UpdateEmptyState();
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryRowView view)
        {
            return;
        }

        // Close first so playback (inline audio, or a new video window owned by the main window) is visible.
        var channelId = view.ChannelId;
        Close();
        await _play(channelId);
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            LocalizationService.Get("HistoryClearConfirm"),
            LocalizationService.Get("HistoryTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.OK)
        {
            return;
        }

        await _clear();
        _rows.Clear();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
