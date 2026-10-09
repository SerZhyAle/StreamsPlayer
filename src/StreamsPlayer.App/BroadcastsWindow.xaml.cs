using System.Collections.ObjectModel;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>The group heading: the device's name and presence (SP-0201 requirement 2).</summary>
internal sealed record BroadcastDeviceHeading(string DeviceName, string PresenceText)
{
    // The list box's item peer takes the accessible name from ToString(), and a record's own prints every
    // member. The heading is read as the device and whether it is on the air - exactly what is on screen.
    public override string ToString() => $"{DeviceName} {PresenceText}";
}

/// <summary>
/// One broadcast of the view. The address is deliberately absent: a relay or tunnel address is the
/// capability to listen, so it is never shown here, never a tooltip, never a title (SP-0201 req 5).
/// </summary>
internal sealed record BroadcastRow(ExchangeBroadcastView View)
{
    public string Title => string.IsNullOrWhiteSpace(View.Record.Title) ? ModeLabel : View.Record.Title!;

    public string Detail => View.Support switch
    {
        ExchangeBroadcastSupport.Unsupported => LocalizationService.Get("BroadcastsUnsupportedReason"),
        ExchangeBroadcastSupport.UnsupportedSchema => LocalizationService.Get("BroadcastsUnsupportedSchema"),
        _ => ModeLabel
    };

    public Visibility ActionsVisibility =>
        View.Support == ExchangeBroadcastSupport.Supported ? Visibility.Visible : Visibility.Collapsed;

    // What a screen reader announces for the row (the item peer's name is ToString()). A record's own
    // prints View, and View carries the descriptor's address - the capability to listen, which this
    // window exists never to expose (SP-0201 req 5). The row is read as its title and its detail line.
    public override string ToString() => $"{Title}, {Detail}";

    private string ModeLabel => View.Record.Mode switch
    {
        FastMediaSorterBroadcastDescriptor.VideoAudioMode => LocalizationService.Get("BroadcastsVideo"),
        FastMediaSorterBroadcastDescriptor.VideoOnlyMode => LocalizationService.Get("BroadcastsVideoOnly"),
        _ => LocalizationService.Get("BroadcastsAudio")
    };
}

/// <summary>
/// SP-0201: the account's live broadcasts, grouped by device with presence. Play and Keep are the two
/// actions of the plan - both run the one import path a link runs, so a directory channel and a link
/// channel can never diverge. The window owns no library state of its own.
/// </summary>
public partial class BroadcastsWindow : Window
{
    private readonly ExchangeSourceService _source;
    private readonly Func<ExchangeBroadcastView, Task> _play;
    private readonly Func<ExchangeBroadcastView, Task> _keep;
    private readonly ObservableCollection<object> _rows = [];

    internal BroadcastsWindow(
        ExchangeSourceService source,
        Func<ExchangeBroadcastView, Task> play,
        Func<ExchangeBroadcastView, Task> keep)
    {
        InitializeComponent();
        _source = source;
        _play = play;
        _keep = keep;
        BroadcastList.ItemsSource = _rows;
        _source.DirectoryChanged += SourceDirectoryChanged;
        Closed += (_, _) => _source.DirectoryChanged -= SourceDirectoryChanged;
        Rebuild();
    }

    private void SourceDirectoryChanged()
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(Rebuild);
        }
    }

    private void Rebuild()
    {
        _rows.Clear();
        var snapshot = _source.Directory;
        foreach (var group in snapshot?.Groups ?? [])
        {
            _rows.Add(new BroadcastDeviceHeading(group.DeviceName,
                LocalizationService.Get(group.IsOnline ? "BroadcastsOnline" : "BroadcastsOffline")));
            foreach (var view in group.Broadcasts)
            {
                _rows.Add(new BroadcastRow(view));
            }
        }

        // A missing snapshot is the source being off or between connections, not an empty account: the
        // exchange's own status line says so, and the empty text stays for a listed-and-empty directory.
        StatusText.Text = snapshot is null ? LocalizationService.Get(_source.StatusKey) : string.Empty;
        StatusText.Visibility = snapshot is null ? Visibility.Visible : Visibility.Collapsed;
        var empty = snapshot is not null && _rows.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        BroadcastList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is BroadcastRow row)
            {
                await _play(row.View);
            }
        }
        catch (Exception exception)
        {
            // Window-qualified: the once-per-handler notice set is keyed by name, and another window has a Play_Click.
            HandlerBoundary.Report($"{nameof(BroadcastsWindow)}.{nameof(Play_Click)}", exception);
        }
    }

    private async void Keep_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is BroadcastRow row)
            {
                await _keep(row.View);
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report($"{nameof(BroadcastsWindow)}.{nameof(Keep_Click)}", exception);
        }
    }
}
