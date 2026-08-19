using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0080: the small always-on-top surface the application shrinks to while radio plays.
/// </summary>
/// <remarks>
/// This window owns no state. Every value it shows is pushed in by <see cref="MainWindow"/> from the
/// funnel that is already the single writer of that value, and every control raises an event the main
/// window answers with the handler it already has. That is the whole answer to the ticket's first
/// risk - two surfaces cannot disagree about the volume, the sleep timer or what is playing, because
/// only one of them knows any of it.
/// <para>
/// The two text lines are pushed as already-rendered strings rather than as a resource key and
/// arguments. It keeps this window out of the localized call-site gate entirely, and it makes the
/// panel follow a language change for free: the main window re-renders both lines on a language
/// change and pushes the result here in the same call.
/// </para>
/// </remarks>
public partial class CompactPanelWindow : Window
{
    // The same shape as MainWindow's _suppressAudioVolumeSave: a pushed value must not travel back as
    // if the listener had just moved this slider.
    private bool _suppressVolumeEcho;

    public CompactPanelWindow() => InitializeComponent();

    public event EventHandler? ExpandRequested;

    public event EventHandler? TransportRequested;

    public event EventHandler? RandomRequested;

    public event EventHandler? SleepTimerRequested;

    public event EventHandler<double>? VolumeChanged;

    public event EventHandler? Moved;

    /// <summary>Raised once the listener lets go of the window, never during the drag.</summary>
    /// <remarks>
    /// The on-screen clamp has to run here rather than on <see cref="Window.LocationChanged"/>: writing
    /// Left/Top while the mouse still owns the move makes the window fight the cursor, and the listener
    /// sees jitter instead of a limit. <c>WM_EXITSIZEMOVE</c> is the one signal that says the modal move
    /// loop has ended, and WPF surfaces no event for it.
    /// </remarks>
    public event EventHandler? MoveFinished;

    /// <summary>The button the main window places its own sleep-timer menu on.</summary>
    public Button SleepTimerAnchor => SleepTimerButton;

    /// <summary>The menu the main window fills, so the presets and the time parser have one home.</summary>
    public ContextMenu SleepTimerMenu => SleepTimerContextMenu;

    public void ShowLines(string nowPlaying, string status, string title)
    {
        NowPlayingText.Text = nowPlaying;
        StatusText.Text = status;
        Title = title;
    }

    /// <summary>
    /// Mirrors <c>MainWindow.ApplyAudioTransportState</c>. The glyph and the caption come from the same
    /// two resources the full window's button uses, so the pair cannot drift; only the template differs,
    /// because a panel this narrow has no width for a caption the tooltip already carries.
    /// </summary>
    public void ShowTransport(bool hasStation, bool playing)
    {
        TransportButton.IsEnabled = hasStation;
        TransportButton.Visibility = hasStation ? Visibility.Visible : Visibility.Collapsed;
        VolumeSlider.Visibility = hasStation ? Visibility.Visible : Visibility.Collapsed;
        TransportButton.Style = (Style)FindResource(playing ? "StopGlyphOnlyButton" : "PlayGlyphOnlyButton");
        var caption = playing ? "StopAudio" : "ResumeAudio";
        TransportButton.SetResourceReference(ToolTipProperty, caption);
        TransportButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, caption);
    }

    public void ShowVolume(double value)
    {
        _suppressVolumeEcho = true;
        try
        {
            VolumeSlider.Value = value;
        }
        finally
        {
            _suppressVolumeEcho = false;
        }
    }

    public void ShowSleepTimer(bool visible, object? content, object? tooltip)
    {
        SleepTimerButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SleepTimerButton.Content = content;
        SleepTimerButton.ToolTip = tooltip;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressVolumeEcho)
        {
            return;
        }

        VolumeChanged?.Invoke(this, e.NewValue);
    }

    private void SleepTimerButton_Click(object sender, RoutedEventArgs e) => SleepTimerRequested?.Invoke(this, EventArgs.Empty);

    private void RandomButton_Click(object sender, RoutedEventArgs e) => RandomRequested?.Invoke(this, EventArgs.Empty);

    private void TransportButton_Click(object sender, RoutedEventArgs e) => TransportRequested?.Invoke(this, EventArgs.Empty);

    private void ExpandButton_Click(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke(this, EventArgs.Empty);

    private void Window_LocationChanged(object? sender, EventArgs e) => Moved?.Invoke(this, EventArgs.Empty);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(ExitSizeMoveHook);
        }
    }

    private IntPtr ExitSizeMoveHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmExitSizeMove = 0x0232;
        if (message == WmExitSizeMove)
        {
            MoveFinished?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }
}
