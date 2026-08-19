using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0080: the compact-panel mode. Collapsing hides this window and shows
/// <see cref="CompactPanelWindow"/>; expanding does the reverse.
/// </summary>
/// <remarks>
/// <para>
/// Hiding rather than closing is what makes acceptance criterion 5 free: nothing is unloaded, so the
/// scroll offset, the active filter and the selected station are still there when the window is shown
/// again, and it comes back at the position it was left at.
/// </para>
/// <para>
/// The panel is deliberately not an owned window - the same choice
/// <c>OpenIndependentPlayerWindow</c> makes, and for a related reason: an owned window carrying its
/// own taskbar button puts two entries where criterion 6 asks for one. Exactly one window is visible
/// at a time instead, so the taskbar and Alt+Tab carry one item in both modes. The cost is that this
/// file owns the panel's lifetime explicitly.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private const double CompactPanelScreenMargin = 24;

    private const uint MonitorDefaultToNearest = 2;

    private CompactPanelWindow? _compactPanel;

    // Session-only, matching the owner's decision that the mode is not remembered across launches:
    // collapsing twice in one session returns the panel to where it was left, and the next launch
    // opens the full window with no memory of either.
    private ScreenRect? _compactPanelPlacement;

    // Distinguishes the close this file performs on the way to the full window from the one the
    // listener performs with the panel's own close button, which ends the application.
    private bool _closingPanelToExpand;

    /// <summary>
    /// Whether the catalog window is hidden behind the panel right now.
    /// </summary>
    /// <remarks>
    /// Read by the two playback paths that would otherwise raise a modal owned by a hidden window. Such
    /// a dialog renders *behind* the always-on-top panel, where it cannot be reached and the application
    /// reads as frozen; and the ticket rules out the obvious alternative of expanding to show it, since
    /// a window jumping over someone else's full-screen work is exactly what the panel exists to avoid.
    /// Both therefore report themselves on the status line the panel already mirrors - the same trade
    /// the resume path has been making since SP-0062.
    /// </remarks>
    private bool IsCompact => _compactPanel is not null;

    private void CompactPanelButton_Click(object sender, RoutedEventArgs e) => CollapseToCompactPanel();

    private void CollapseToCompactPanel()
    {
        if (_compactPanel is not null)
        {
            return;
        }

        var panel = new CompactPanelWindow();
        panel.ExpandRequested += CompactPanel_ExpandRequested;
        panel.TransportRequested += CompactPanel_TransportRequested;
        panel.RandomRequested += CompactPanel_RandomRequested;
        panel.SleepTimerRequested += CompactPanel_SleepTimerRequested;
        panel.VolumeChanged += CompactPanel_VolumeChanged;
        panel.Moved += CompactPanel_Moved;
        panel.MoveFinished += CompactPanel_MoveFinished;
        panel.Closed += CompactPanel_Closed;
        _compactPanel = panel;

        // Placed twice on purpose. Before Show the panel has no window handle and therefore no DPI of
        // its own, so this first pass borrows the catalog's - right on a single-scale desktop, and close
        // enough elsewhere that the window does not appear somewhere absurd. The second pass runs once
        // the panel is real and re-clamps against the monitor it actually landed on.
        var wanted = _compactPanelPlacement ?? DefaultCompactPanelPlacement(panel);
        var preview = ScreenPlacement.Clamp(wanted, WorkAreaAround(wanted, this));
        panel.Left = preview.Left;
        panel.Top = preview.Top;
        SystemEvents.DisplaySettingsChanged += CompactPanel_DisplaySettingsChanged;

        panel.Show();
        ApplyCompactPanelPlacement(panel, Placement(panel));
        UpdateCompactPanel();
        Hide();
        _log.Event("COMPACT PANEL", "state=collapsed");
    }

    private void ExpandFromCompactPanel()
    {
        if (_compactPanel is not { } panel)
        {
            return;
        }

        _closingPanelToExpand = true;
        panel.Close();
        Show();
        Activate();
        _log.Event("COMPACT PANEL", "state=expanded");
    }

    private void CompactPanel_Closed(object? sender, EventArgs e)
    {
        if (sender is not CompactPanelWindow panel)
        {
            return;
        }

        _compactPanelPlacement = Placement(panel);
        panel.ExpandRequested -= CompactPanel_ExpandRequested;
        panel.TransportRequested -= CompactPanel_TransportRequested;
        panel.RandomRequested -= CompactPanel_RandomRequested;
        panel.SleepTimerRequested -= CompactPanel_SleepTimerRequested;
        panel.VolumeChanged -= CompactPanel_VolumeChanged;
        panel.Moved -= CompactPanel_Moved;
        panel.MoveFinished -= CompactPanel_MoveFinished;
        panel.Closed -= CompactPanel_Closed;
        // Subscribed only while a panel exists. A permanent SystemEvents subscription is a
        // process-wide leak - the theme service already paid for that lesson.
        SystemEvents.DisplaySettingsChanged -= CompactPanel_DisplaySettingsChanged;
        _compactPanel = null;

        if (_closingPanelToExpand)
        {
            _closingPanelToExpand = false;
            return;
        }

        // The listener closed the only visible window of the application, which is what closing an
        // application means. Route it through the catalog window so the ordinary Closing/Closed path -
        // the browsing session, the resume record, the log - runs exactly as it does from the full view.
        Close();
    }

    /// <summary>Closes the panel during teardown, without letting that close read as "quit".</summary>
    /// <remarks>
    /// Called from <c>MainWindow_Closing</c> beside <c>CloseOpenPlayerWindows</c>, and for the reason
    /// recorded there: the catalog must still be open when its last companion window goes, or the
    /// default <c>OnLastWindowClose</c> shutdown fires before the state has been saved.
    /// </remarks>
    private void CloseCompactPanel()
    {
        if (_compactPanel is null)
        {
            return;
        }

        _closingPanelToExpand = true;
        _compactPanel.Close();
    }

    private void CompactPanel_ExpandRequested(object? sender, EventArgs e) => ExpandFromCompactPanel();

    private void CompactPanel_TransportRequested(object? sender, EventArgs e) => ToggleAudioTransport();

    private async void CompactPanel_RandomRequested(object? sender, EventArgs e) => await StartRandomStationHuntAsync();

    private void CompactPanel_SleepTimerRequested(object? sender, EventArgs e)
    {
        if (_compactPanel is not { } panel)
        {
            return;
        }

        // The same builder the full window uses, so the presets, the inline time entry and the cancel
        // entry have one home and cannot drift between the two surfaces.
        BuildSleepTimerMenu(panel.SleepTimerMenu);
        panel.SleepTimerMenu.PlacementTarget = panel.SleepTimerAnchor;
        panel.SleepTimerMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        panel.SleepTimerMenu.IsOpen = true;
    }

    // Written into the full window's slider rather than applied directly: that slider is the owner of
    // the value, and its ValueChanged is the one place the volume reaches the player and the state.
    private void CompactPanel_VolumeChanged(object? sender, double value) => AudioVolumeSlider.Value = value;

    private void CompactPanel_Moved(object? sender, EventArgs e)
    {
        if (_compactPanel is { } panel)
        {
            _compactPanelPlacement = Placement(panel);
        }
    }

    // The listener has let go. Clamping only now is what keeps the limit from reading as jitter: a
    // Left/Top written while the move loop still owns the window makes it fight the cursor.
    private void CompactPanel_MoveFinished(object? sender, EventArgs e)
    {
        if (_compactPanel is { } panel)
        {
            ApplyCompactPanelPlacement(panel, Placement(panel));
            _compactPanelPlacement = Placement(panel);
        }
    }

    // A monitor can disappear while the panel stands on it. The clamp below answers that by asking for
    // the nearest surviving monitor, so the panel comes back rather than staying on a screen that is
    // no longer attached.
    private void CompactPanel_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_compactPanel is { } panel)
        {
            ApplyCompactPanelPlacement(panel, Placement(panel));
        }
    }

    /// <summary>
    /// The single push into the panel. Reads the full window's own controls rather than the fields
    /// behind them, which is what guarantees the two surfaces say the same words: there is no second
    /// rendering of anything, only a copy of the first.
    /// </summary>
    private void UpdateCompactPanel()
    {
        if (_compactPanel is not { } panel)
        {
            return;
        }

        panel.ShowLines(NowPlayingText.Text, StatusText.Text, Title);
        panel.ShowTransport(_playingAudio is not null || _audioPausedChannel is not null, _playingAudio is not null);
        panel.ShowVolume(AudioVolumeSlider.Value);
        panel.ShowSleepTimer(
            SleepTimerButton.Visibility == Visibility.Visible,
            SleepTimerButton.Content,
            SleepTimerButton.ToolTip);
    }

    private static ScreenRect Placement(Window window) =>
        new(window.Left,
            window.Top,
            window.ActualWidth > 0 ? window.ActualWidth : window.Width,
            window.ActualHeight > 0 ? window.ActualHeight : window.Height);

    private void ApplyCompactPanelPlacement(Window panel, ScreenRect wanted)
    {
        var clamped = ScreenPlacement.Clamp(wanted, WorkAreaAround(wanted, panel));
        panel.Left = clamped.Left;
        panel.Top = clamped.Top;
    }

    // The bottom trailing corner of the monitor the catalog is on: out of the way of the work the
    // listener goes back to, and the corner a small always-on-top strip is expected in.
    private ScreenRect DefaultCompactPanelPlacement(Window panel)
    {
        var work = WorkAreaAround(Placement(this), this);
        return new ScreenRect(
            work.Right - panel.Width - CompactPanelScreenMargin,
            work.Bottom - panel.Height - CompactPanelScreenMargin,
            panel.Width,
            panel.Height);
    }

    /// <summary>
    /// The work area of the monitor nearest the given rectangle, in device-independent units.
    /// </summary>
    /// <remarks>
    /// <c>MONITOR_DEFAULTTONEAREST</c> is the whole answer to a switched-off monitor: it returns the
    /// nearest surviving one, and the clamp then brings the panel onto it. Falls back to
    /// <see cref="SystemParameters.WorkArea"/> - the primary monitor - when the window has no
    /// presentation source yet or the call fails. That is a worse answer than the real one, never a
    /// wrong one: the fallback is still a real work area.
    /// <para>
    /// <paramref name="dpiSource"/> is the window the rectangle belongs to, not always this one: the two
    /// can sit on monitors with different scaling, and converting the panel's position through the
    /// catalog's transform would land it in the wrong place by exactly that ratio.
    /// </para>
    /// </remarks>
    private static ScreenRect WorkAreaAround(ScreenRect rectangle, Visual dpiSource)
    {
        var fallback = SystemParameters.WorkArea;
        var primary = new ScreenRect(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
        if (PresentationSource.FromVisual(dpiSource)?.CompositionTarget is not { } target)
        {
            return primary;
        }

        var topLeft = target.TransformToDevice.Transform(new Point(rectangle.Left, rectangle.Top));
        var bottomRight = target.TransformToDevice.Transform(new Point(rectangle.Right, rectangle.Bottom));
        var device = new NativeRect
        {
            Left = (int)Math.Floor(topLeft.X),
            Top = (int)Math.Floor(topLeft.Y),
            Right = (int)Math.Ceiling(bottomRight.X),
            Bottom = (int)Math.Ceiling(bottomRight.Y)
        };

        var monitor = MonitorFromRect(ref device, MonitorDefaultToNearest);
        var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return primary;
        }

        var workTopLeft = target.TransformFromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
        var workBottomRight = target.TransformFromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new ScreenRect(
            workTopLeft.X,
            workTopLeft.Y,
            workBottomRight.X - workTopLeft.X,
            workBottomRight.Y - workTopLeft.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rectangle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);
}
