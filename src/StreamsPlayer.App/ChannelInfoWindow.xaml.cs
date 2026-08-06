using System.Runtime.InteropServices;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0053: everything the app knows about one channel, plus what its stream was measured to send.
/// </summary>
/// <remarks>
/// Read-only by contract: it writes no state, records no outcome, and changes nothing about the
/// channel. The measurement is the only thing here that touches the network, it runs once, and closing
/// the window cancels it.
/// </remarks>
public partial class ChannelInfoWindow : Window
{
    private readonly StreamChannel _channel;
    private readonly IReadOnlyList<string> _collectionNames;
    private readonly Func<StreamTransmission?>? _liveTransmission;
    private readonly CancellationTokenSource _measurement = new();
    private IReadOnlyList<ChannelFact> _storedFacts = [];
    private IReadOnlyList<ChannelFact> _streamFacts = [];

    /// <param name="liveTransmission">
    /// Supplied when this channel is already playing: the engine on screen answers the question, so
    /// nothing is opened. Null means the window measures the stream itself.
    /// </param>
    public ChannelInfoWindow(
        StreamChannel channel,
        IReadOnlyList<string> collectionNames,
        Func<StreamTransmission?>? liveTransmission)
    {
        InitializeComponent();
        _channel = channel;
        _collectionNames = collectionNames;
        _liveTransmission = liveTransmission;
        Title = LocalizationService.Format("WindowTitleWithSubject", LocalizationService.Get("AboutChannelTitle"), channel.Title);
        Loaded += ChannelInfoWindow_Loaded;
        Closed += ChannelInfoWindow_Closed;
    }

    private async void ChannelInfoWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _storedFacts = ChannelFactSheet.Describe(_channel, _collectionNames);
        ChannelFacts.ItemsSource = Rows(ChannelFactGroup.Channel);
        CatalogFacts.ItemsSource = Rows(ChannelFactGroup.Catalog);
        ShowTransmission([ChannelFactSheet.Status("AboutMeasuring")]);

        var live = _liveTransmission?.Invoke();
        if (live is not null)
        {
            ShowTransmission(ChannelFactSheet.DescribeTransmission(live, measured: true));
            return;
        }

        var measured = await StreamTransmissionProbe.MeasureAsync(_channel.Url, _measurement.Token);
        if (_measurement.IsCancellationRequested)
        {
            return; // the window is gone; there is nothing left to tell
        }

        ShowTransmission(ChannelFactSheet.DescribeTransmission(measured, measured is not null));
    }

    private void ChannelInfoWindow_Closed(object? sender, EventArgs e)
    {
        _measurement.Cancel();
        _measurement.Dispose();
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ChannelFactSheet.Render([.. _storedFacts, .. _streamFacts], LocalizationService.Get));
            MessageBox.Show(this, LocalizationService.Get("AboutCopied"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (COMException)
        {
            // Another process owns the clipboard; the same failure SettingsWindow reports for its own copy.
            MessageBox.Show(this, LocalizationService.Get("AboutCopyFailed"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowTransmission(IReadOnlyList<ChannelFact> facts)
    {
        _streamFacts = facts;
        StreamFacts.ItemsSource = facts.Select(ToRow).ToArray();
    }

    private FactRow[] Rows(ChannelFactGroup group) =>
        _storedFacts.Where(fact => fact.Group == group).Select(ToRow).ToArray();

    private static FactRow ToRow(ChannelFact fact) => new(
        LocalizationService.Get(fact.LabelKey),
        ChannelFactSheet.ResolveValue(fact, LocalizationService.Get));

    private sealed record FactRow(string Label, string Value);
}
