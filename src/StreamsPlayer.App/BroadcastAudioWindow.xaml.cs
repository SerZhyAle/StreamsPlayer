using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0222 requirement 3: an audio-only incoming broadcast in a window of its own, with its own volume and its
/// own stop. A companion surface (APP-BEHAVIOUR rule 1): it stays open beside the main window and the other
/// broadcasts, it is not modal and not owned, and the close box, Escape and Close are the one path - they stop
/// this broadcast and nothing else. The volume is this window's for as long as it is open and is not saved: the
/// saved audio volume is the main window's station's, and a broadcast beside it must not move it.
/// </summary>
public partial class BroadcastAudioWindow : Window
{
    private readonly BroadcastAudioSession _session;
    private readonly string _title;
    private int _savedVolume;
    private bool _loading = true;

    internal BroadcastAudioWindow(StreamChannel channel, BroadcastAudioSession session, int volume)
    {
        InitializeComponent();
        _session = session;
        _title = StreamTitleFormatter.Display(channel.Title);
        Title = _title;
        TitleText.Text = _title;
        _savedVolume = Math.Clamp(volume, 0, 100);
        VolumeSlider.Value = _savedVolume;
        _loading = false;
        Channel = channel;
        _session.Changed += SessionChanged;
        Closed += (_, _) =>
        {
            _session.Changed -= SessionChanged;
            _session.Dispose();
        };
        Refresh();
    }

    internal StreamChannel Channel { get; }

    /// <summary>
    /// Several broadcasts open at once must not land on one another: each window takes a step down and to the
    /// right of the one before, so every title and its Stop stay in view. Called before the window is shown.
    /// </summary>
    internal void PlaceCascaded(int index)
    {
        const double Step = 32;
        var area = SystemParameters.WorkArea;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2) + index * Step;
        Top = area.Top + 96 + index * Step;
    }

    internal BroadcastAudioSession Session => _session;

    /// <summary>Starts listening; the caller opens the window and then asks for the sound.</summary>
    internal void StartListening() => _session.Start();

    private void SessionChanged()
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(Refresh);
        }
    }

    private void Refresh()
    {
        var stateKey = _session.State switch
        {
            BroadcastLegState.Connecting => "BroadcastsStateConnecting",
            BroadcastLegState.Playing => "BroadcastsStatePlaying",
            BroadcastLegState.Reconnecting => "BroadcastsStateReconnecting",
            BroadcastLegState.Ended => "BroadcastsStateEnded",
            BroadcastLegState.Failed => "BroadcastsStateFailed",
            _ => "BroadcastsStateStopped"
        };
        var state = LocalizationService.Get(stateKey);
        StateText.Text = _session.MessageKey is { } key
            ? state + " " + (key == IncomingBroadcastFailure.BoundReachedKey
                ? LocalizationService.Format(key, IncomingBroadcastWindows.Default.Maximum)
                : LocalizationService.Format(key, _title))
            : state;
        // SP-0225: a leg that could not be kept offers the connection check - for the generic "no longer available"
        // line only; a refused password or a certificate that does not match already says what to do.
        DiagnoseButton.Visibility = _session is { State: BroadcastLegState.Failed, MessageKey: IncomingBroadcastFailure.NoLongerAvailableKey }
            && _session.DiagnosisTarget() is not null ? Visibility.Visible : Visibility.Collapsed;
        var active = _session.IsActive;
        StopButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _savedVolume = (int)Math.Round(e.NewValue);
        ApplyVolume();
    }

    private void MuteBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            ApplyVolume();
        }
    }

    private void ApplyVolume() => _session.Volume = MuteBox.IsChecked == true ? 0 : _savedVolume;

    // A companion surface is not a dialog, so there is no cancel role to lean on: Escape, the close box and
    // Close are one path, written here once.
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_session.DiagnosisTarget() is { } target)
            {
                var request = new ExchangeDiagnosisRequest(ExchangeFailureRows.Unreachable,
                    IncomingBroadcastFailure.NoLongerAvailableKey, target.Host, target.Port, false, _title);
                new ExchangeDiagnosisWindow(request) { Owner = this }.ShowDialog();
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report($"{nameof(BroadcastAudioWindow)}.{nameof(Diagnose_Click)}", exception);
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _session.Start();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report($"{nameof(BroadcastAudioWindow)}.{nameof(Play_Click)}", exception);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _session.Stop();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report($"{nameof(BroadcastAudioWindow)}.{nameof(Stop_Click)}", exception);
        }
    }
}
