using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// Presents a <see cref="WaveParticlesBackdropSession"/> behind a surface's content (SP-0110,
/// <c>WAVE-PARTICLES</c>). The surface decides nothing about the motion itself; it declares its intent
/// (rule 11), how far it is dimmed (rule 16) and the background colour the trail fades into.
/// </summary>
/// <remarks>
/// <para>Draws frames only while somebody can see them (rule 10): the element visible, its window not
/// minimized, and - inside a scrolling list - inside the viewport. Hidden, it advances no clock, and
/// coming back resumes the same session.</para>
/// <para>Motion is replaced by the settled still frame when a power policy freezes this intent (rule 9),
/// and the backdrop disappears entirely in high contrast mode. Windows "Animation effects" deliberately
/// does not stop it: the owner decided on 2026-09-23 (SP-0110) that the app's own "Animated background"
/// switch is the one that governs, and the difference from rule 9 is a dated exception in the contract
/// registry.</para>
/// <para>Ticks on a dispatcher timer rather than on every composition frame: subscribing to the
/// composition clock keeps WPF composing at the display's full rate for the whole window, and the
/// contract's elapsed-time pacing makes the lower rate look the same.</para>
/// </remarks>
public sealed class WaveParticlesBackdrop : FrameworkElement
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 30);

    // A stall longer than this (a modal loop, a debugger) is not replayed as one giant step.
    private const double MaximumStepSeconds = 0.25;

    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(
        nameof(Session), typeof(object), typeof(WaveParticlesBackdrop),
        new FrameworkPropertyMetadata(null, OnSessionChanged));

    public static readonly DependencyProperty IntentProperty = DependencyProperty.Register(
        nameof(Intent), typeof(WaveParticlesIntent), typeof(WaveParticlesBackdrop),
        new FrameworkPropertyMetadata(WaveParticlesIntent.Decorative, OnStateChanged));

    public static readonly DependencyProperty IntensityProperty = DependencyProperty.Register(
        nameof(Intensity), typeof(double), typeof(WaveParticlesBackdrop),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WashBrushProperty = DependencyProperty.Register(
        nameof(WashBrush), typeof(Brush), typeof(WaveParticlesBackdrop),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(WaveParticlesBackdrop),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Color DarkWash = Color.FromRgb(
        WaveParticlesConstants.DarkWashLevel, WaveParticlesConstants.DarkWashLevel, WaveParticlesConstants.DarkWashLevel);

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private Window? _window;
    private ScrollViewer? _scrollViewer;
    private bool _loaded;

    public WaveParticlesBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
        _timer.Tick += Timer_Tick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateTicking();
        SizeChanged += (_, _) => UpdateTicking();
    }

    /// <summary>The session to present, or null for no backdrop. Typed as object so XAML can bind it.</summary>
    public object? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public WaveParticlesIntent Intent
    {
        get => (WaveParticlesIntent)GetValue(IntentProperty);
        set => SetValue(IntentProperty, value);
    }

    /// <summary>Rule 15's intensity: the opacity of the final present, clamped to <c>[0, 1]</c>.</summary>
    public double Intensity
    {
        get => (double)GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }

    /// <summary>The surface's own background; the trail fades into it and it decides light or dark (rule 7).</summary>
    public Brush? WashBrush
    {
        get => (Brush?)GetValue(WashBrushProperty);
        set => SetValue(WashBrushProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private WaveParticlesBackdropSession? CurrentSession => Session as WaveParticlesBackdropSession;

    // Pure ornament: nothing for a screen reader to announce and nothing to focus.
    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var session = CurrentSession;
        var (width, height) = BufferSize();
        if (session is null || BackdropEnvironment.HighContrast || width < 1 || height < 1)
        {
            return;
        }

        var wash = WashColour();
        if (!_timer.IsEnabled)
        {
            session.EnsureStill(width, height, wash, IsLight(wash));
        }
        else
        {
            session.EnsureBuffer(width, height, wash);
        }

        if (session.Frame is not { } frame)
        {
            return;
        }

        var bounds = new Rect(RenderSize);
        var radius = CornerRadius;
        if (radius > 0)
        {
            drawingContext.PushClip(new RectangleGeometry(bounds, radius, radius));
        }

        drawingContext.PushOpacity(Math.Clamp(Intensity, WaveParticlesConstants.IntensityMin, WaveParticlesConstants.IntensityMax));
        drawingContext.DrawImage(frame, bounds);
        drawingContext.Pop();
        if (radius > 0)
        {
            drawingContext.Pop();
        }
    }

    private static void OnSessionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var backdrop = (WaveParticlesBackdrop)d;
        if (e.OldValue is WaveParticlesBackdropSession old)
        {
            old.RunningChanged -= backdrop.Session_RunningChanged;
        }

        if (e.NewValue is WaveParticlesBackdropSession current && backdrop._loaded)
        {
            current.RunningChanged += backdrop.Session_RunningChanged;
        }

        backdrop.UpdateTicking();
        backdrop.InvalidateVisual();
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((WaveParticlesBackdrop)d).UpdateTicking();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        BackdropEnvironment.EnsureInitialized();
        BackdropEnvironment.Changed += Environment_Changed;
        if (CurrentSession is { } session)
        {
            session.RunningChanged += Session_RunningChanged;
        }

        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.StateChanged += Window_StateChanged;
        }

        _scrollViewer = FindScrollViewer();
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
        }

        UpdateTicking();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        BackdropEnvironment.Changed -= Environment_Changed;
        if (CurrentSession is { } session)
        {
            session.RunningChanged -= Session_RunningChanged;
        }

        if (_window is not null)
        {
            _window.StateChanged -= Window_StateChanged;
            _window = null;
        }

        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged -= ScrollViewer_ScrollChanged;
            _scrollViewer = null;
        }

        UpdateTicking();
    }

    private void Session_RunningChanged(object? sender, EventArgs e) => UpdateTicking();

    private void Environment_Changed(object? sender, EventArgs e)
    {
        UpdateTicking();
        InvalidateVisual();
    }

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateTicking();

    private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateTicking();

    private void UpdateTicking()
    {
        var shouldTick = _loaded
            && CurrentSession is { IsRunning: true }
            && IsVisible
            && _window?.WindowState != WindowState.Minimized
            && !BackdropEnvironment.HighContrast
            && BackdropEnvironment.AnimationEffectsEnabled
            && !BackdropEnvironment.PowerFreezes(Intent)
            && IsInViewport();
        if (shouldTick == _timer.IsEnabled)
        {
            return;
        }

        if (shouldTick)
        {
            _clock.Restart();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _clock.Reset();
            InvalidateVisual();
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var session = CurrentSession;
        var (width, height) = BufferSize();
        if (session is null || width < 1 || height < 1)
        {
            return;
        }

        var elapsed = Math.Min(_clock.Elapsed.TotalSeconds, MaximumStepSeconds);
        _clock.Restart();
        var wash = WashColour();
        session.Step(elapsed, width, height, wash, IsLight(wash));
        InvalidateVisual();
    }

    private bool IsInViewport()
    {
        if (_scrollViewer is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return true;
        }

        try
        {
            var bounds = TransformToAncestor(_scrollViewer).TransformBounds(new Rect(RenderSize));
            return bounds.IntersectsWith(new Rect(_scrollViewer.RenderSize));
        }
        catch (InvalidOperationException)
        {
            // Recycled out from under the list between a scroll event and this check.
            return false;
        }
    }

    private ScrollViewer? FindScrollViewer()
    {
        DependencyObject? node = VisualTreeHelper.GetParent(this);
        while (node is not null)
        {
            if (node is ScrollViewer viewer)
            {
                return viewer;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private (int Width, int Height) BufferSize() =>
        ((int)Math.Round(ActualWidth), (int)Math.Round(ActualHeight));

    private Color WashColour() => WashBrush is SolidColorBrush solid ? solid.Color : DarkWash;

    private static bool IsLight(Color colour) =>
        ((0.2126 * colour.R) + (0.7152 * colour.G) + (0.0722 * colour.B)) / 255 > 0.5;
}
