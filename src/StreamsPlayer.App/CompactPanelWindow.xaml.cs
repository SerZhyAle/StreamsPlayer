using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

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
/// The header and its tooltip are pushed as already-rendered strings rather than as a resource key and
/// arguments. It keeps this window out of the localized call-site gate entirely, and it makes the
/// panel follow a language change for free: the main window re-renders every line on a language
/// change and pushes the result here in the same call.
/// </para>
/// <para>
/// The window has no system caption, so it carries its own close, minimize and always-on-top buttons
/// and is dragged by any spot outside a control. The pin state is the one thing it shows that the
/// main window owns only for the panel's sake; it still lives there, so a collapse after an expand
/// comes back pinned the way it was left.
/// </para>
/// </remarks>
public partial class CompactPanelWindow : Window
{
    // The same shape as MainWindow's _suppressAudioVolumeSave: a pushed value must not travel back as
    // if the listener had just moved this slider.
    private bool _suppressVolumeEcho;

    public CompactPanelWindow() => InitializeComponent();

    public event EventHandler? ExpandRequested;

    public event EventHandler? PreviousRequested;

    public event EventHandler? NextRequested;

    public event EventHandler? TransportRequested;

    public event EventHandler? RandomRequested;

    public event EventHandler? RecordRequested;

    public event EventHandler? SleepTimerRequested;

    public event EventHandler? TopmostToggleRequested;

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

    /// <summary>
    /// The header line: the station and its tags. The now-playing and status lines have no room of
    /// their own in a captionless strip, so they travel in the header's tooltip.
    /// </summary>
    /// <param name="station">The station title, or the full window's now-playing line when nothing is loaded.</param>
    /// <param name="tags">The station's tags; empty hides the separator as well.</param>
    public void ShowHeader(string station, string tags, string nowPlaying, string status, string title)
    {
        StationTitleRun.Text = station;
        TagsRun.Text = tags;
        TagsSeparatorRun.Text = tags.Length == 0 ? string.Empty : "    ";
        HeaderText.ToolTip = string.Join(Environment.NewLine,
            new[] { nowPlaying, status }.Where(line => !string.IsNullOrWhiteSpace(line)));
        Title = title;
    }

    /// <summary>Applies the pin state the main window owns; the button only asks for a flip.</summary>
    public void ShowTopmost(bool topmost)
    {
        Topmost = topmost;
        TopmostButton.Style = (Style)FindResource(topmost ? "PinOnGlyphOnlyButton" : "PinOffGlyphOnlyButton");
        var caption = topmost ? "CompactPanelOnTopOn" : "CompactPanelOnTopOff";
        TopmostButton.SetResourceReference(ToolTipProperty, caption);
        TopmostButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, caption);
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

    public void ShowRecording(bool hasStation, bool isRecording)
    {
        RecordButton.Visibility = hasStation ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.Style = (Style)FindResource(isRecording ? "StopRecordGlyphOnlyButton" : "RecordGlyphOnlyButton");
        var tip = isRecording ? "StopRecordTip" : "RecordTip";
        var name = isRecording ? "StopRecord" : "Record";
        RecordButton.SetResourceReference(ToolTipProperty, tip);
        RecordButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, name);
    }

    public void ShowNavigation(bool hasStation, bool canPrevious, bool canNext)
    {
        PrevStationButton.Visibility = hasStation ? Visibility.Visible : Visibility.Collapsed;
        NextStationButton.Visibility = hasStation ? Visibility.Visible : Visibility.Collapsed;
        PrevStationButton.IsEnabled = canPrevious;
        NextStationButton.IsEnabled = canNext;
    }

    public void ShowChannel(ChannelRow? row, bool playing)
    {
        if (row is null)
        {
            FaviconImage.Source = null;
            FaviconImage.Visibility = Visibility.Collapsed;
            MonogramPlate.Visibility = Visibility.Collapsed;
            PlayingIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        if (row.Favicon is not null)
        {
            FaviconImage.Source = row.Favicon;
            FaviconImage.Visibility = Visibility.Visible;
            MonogramPlate.Visibility = Visibility.Collapsed;
        }
        else
        {
            FaviconImage.Source = null;
            FaviconImage.Visibility = Visibility.Collapsed;
            MonogramPlate.Visibility = Visibility.Visible;
            MonogramPlate.Background = row.MonogramBrush;
            MonogramText.Text = row.MonogramText;
            MonogramText.Foreground = row.MonogramForeground;
            CountryCodeBadge.Visibility = row.CountryCodeVisibility;
            CountryCodeText.Text = row.CountryCode;
            CountryCodeText.Foreground = row.MonogramForeground;
        }

        PlayingIndicator.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>SP-0110: the station's backdrop - running while it plays, frozen after Stop, null with no station.</summary>
    public void ShowBackdrop(WaveParticlesBackdropSession? session) => Backdrop.Session = session;

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

    private void PrevStationButton_Click(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void NextStationButton_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);

    private void RandomButton_Click(object sender, RoutedEventArgs e) => RandomRequested?.Invoke(this, EventArgs.Empty);

    private void RecordButton_Click(object sender, RoutedEventArgs e) => RecordRequested?.Invoke(this, EventArgs.Empty);

    private void TransportButton_Click(object sender, RoutedEventArgs e) => TransportRequested?.Invoke(this, EventArgs.Empty);

    private void ExpandButton_Click(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke(this, EventArgs.Empty);

    private void TopmostButton_Click(object sender, RoutedEventArgs e) => TopmostToggleRequested?.Invoke(this, EventArgs.Empty);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // The same close the system caption used to perform: MainWindow reads a close it did not start
    // as "quit" and routes it through its own Closing path.
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>With no caption, any spot that is not a control is the handle the window moves by.</summary>
    /// <remarks>
    /// Buttons mark the press handled, so it never reaches here; the slider does not always, which is
    /// what the ancestor walk is for. <see cref="Window.DragMove"/> runs the system move loop, so the
    /// <c>WM_EXITSIZEMOVE</c> hook below still ends every drag with <see cref="MoveFinished"/>.
    /// </remarks>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || IsInsideControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DragMove();
    }

    private static bool IsInsideControl(DependencyObject? source)
    {
        for (var node = source; node is not null; node = node is Visual or Visual3D
                 ? VisualTreeHelper.GetParent(node)
                 : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or Slider)
            {
                return true;
            }
        }

        return false;
    }

    private void Window_LocationChanged(object? sender, EventArgs e) => Moved?.Invoke(this, EventArgs.Empty);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(ExitSizeMoveHook);
            RoundCorners(source.Handle);
        }
    }

    // Windows 11 rounds a captioned window itself but leaves a captionless one square; this asks DWM
    // for the same rounding. Earlier Windows versions do not know the attribute and return an error
    // that changes nothing, which is the right outcome there.
    private static void RoundCorners(IntPtr hwnd)
    {
        const int DwmwaWindowCornerPreference = 33;
        var preference = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
