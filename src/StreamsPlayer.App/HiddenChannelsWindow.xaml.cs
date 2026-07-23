using System.Collections.ObjectModel;
using System.Windows;

namespace StreamsPlayer.App;

internal sealed record HiddenChannelView(string Title, string RedactedUrl, string Url);

// SP-0020: lists the user's hidden catalog channels and lets them unhide one at a time. Unhide only ever
// removes a URL from the hidden set; the channel record (pin/order/collection/play-mark) is untouched.
public partial class HiddenChannelsWindow : Window
{
    private readonly Func<string, Task> _unhide;
    private readonly ObservableCollection<HiddenChannelView> _rows;

    internal HiddenChannelsWindow(IReadOnlyList<HiddenChannelView> rows, Func<string, Task> unhide)
    {
        InitializeComponent();
        _unhide = unhide;
        _rows = new ObservableCollection<HiddenChannelView>(rows);
        HiddenList.ItemsSource = _rows;
        UpdateEmptyState();
    }

    private async void Unhide_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HiddenChannelView view)
        {
            return;
        }

        await _unhide(view.Url);
        _rows.Remove(view);
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HiddenList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
