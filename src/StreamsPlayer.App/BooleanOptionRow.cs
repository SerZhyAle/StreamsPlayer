using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0231 (<c>BOOLEAN-OPTION</c> 2.0): one independent yes/no preference as a compact row - a checkbox at the reading
/// start, and beside it a heading (optional glyph, caption, optional help) with the explanation below.
/// </summary>
/// <remarks>
/// The checkbox, the caption and the glyph toggle the option, once, and nothing else does: the explanation, the gaps,
/// the background and the help button are outside the activation region. The row exposes one named checkbox whose
/// description is the explanation, so assistive technology meets one option and not a checkbox and a competing label.
/// <para>
/// The help is optional and comes in two sizes from one authored pair: the summary shows on hover or keyboard focus and
/// stays open while the pointer is on it or on the help button, and Escape closes it; the detail opens on a click, a tap
/// or Enter and stays until it is closed. Neither opens a modal window, takes the focus from the checkbox on hover or
/// changes the option; the detail hands the focus back to the help button when it closes.
/// </para>
/// <para>
/// <see cref="Toggled"/> is raised for a user's activation only. Setting <see cref="IsChecked"/> from code - populating
/// the page, applying a dependency - raises nothing, which is the guard the old <c>Click</c> handlers needed a flag for.
/// </para>
/// </remarks>
public class BooleanOptionRow : Control
{
    private static readonly TimeSpan SummaryCloseDelay = TimeSpan.FromMilliseconds(250);

    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked), typeof(bool), typeof(BooleanOptionRow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(BooleanOptionRow), new PropertyMetadata(string.Empty, OnCaptionChanged));

    public static readonly DependencyProperty ExplanationProperty = DependencyProperty.Register(
        nameof(Explanation), typeof(string), typeof(BooleanOptionRow), new PropertyMetadata(string.Empty, OnPartTextChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(Geometry), typeof(BooleanOptionRow), new PropertyMetadata(null, OnPartTextChanged));

    public static readonly DependencyProperty HelpSummaryProperty = DependencyProperty.Register(
        nameof(HelpSummary), typeof(string), typeof(BooleanOptionRow), new PropertyMetadata(string.Empty, OnPartTextChanged));

    public static readonly DependencyProperty HelpDetailProperty = DependencyProperty.Register(
        nameof(HelpDetail), typeof(string), typeof(BooleanOptionRow), new PropertyMetadata(string.Empty, OnPartTextChanged));

    public static readonly RoutedEvent ToggledEvent = EventManager.RegisterRoutedEvent(
        nameof(Toggled), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(BooleanOptionRow));

    private CheckBox? _check;
    private Button? _help;
    private Popup? _summary;
    private Popup? _detail;
    private Button? _detailClose;
    private DispatcherTimer? _summaryTimer;
    private bool _returnFocusToHelp;

    static BooleanOptionRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(BooleanOptionRow), new FrameworkPropertyMetadata(typeof(BooleanOptionRow)));
    }

    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    /// <summary>The short option name; also the checkbox's accessible name.</summary>
    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>What turning it on or off does, and any prerequisite. Empty for an option the caption already explains.</summary>
    public string Explanation
    {
        get => (string)GetValue(ExplanationProperty);
        set => SetValue(ExplanationProperty, value);
    }

    /// <summary>The option's <c>ICON-SET</c> glyph, when a meaning exists; no empty slot is reserved without one.</summary>
    public Geometry? Glyph
    {
        get => (Geometry?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>The hover and keyboard-focus text. Must not contradict or omit a critical consequence of <see cref="HelpDetail"/>.</summary>
    public string HelpSummary
    {
        get => (string)GetValue(HelpSummaryProperty);
        set => SetValue(HelpSummaryProperty, value);
    }

    /// <summary>The full explanation of both states, prerequisites and dependencies, opened on activation.</summary>
    public string HelpDetail
    {
        get => (string)GetValue(HelpDetailProperty);
        set => SetValue(HelpDetailProperty, value);
    }

    /// <summary>Puts the keyboard focus on the option's checkbox - the one editor of the row.</summary>
    public bool FocusEditor() => _check?.Focus() ?? false;

    /// <summary>Raised once per deliberate activation of the checkbox, the caption or the glyph.</summary>
    public event RoutedEventHandler Toggled
    {
        add => AddHandler(ToggledEvent, value);
        remove => RemoveHandler(ToggledEvent, value);
    }

    public override void OnApplyTemplate()
    {
        Detach();
        base.OnApplyTemplate();

        _check = GetTemplateChild("PART_Check") as CheckBox;
        _help = GetTemplateChild("PART_Help") as Button;
        _summary = GetTemplateChild("PART_SummaryPopup") as Popup;
        _detail = GetTemplateChild("PART_DetailPopup") as Popup;
        _detailClose = GetTemplateChild("PART_DetailClose") as Button;

        if (_check is not null)
        {
            // Clicking the box toggles it natively; the row only reports it. Heading activation flips the box
            // from code and reports it once itself, so the two paths never both fire for one gesture.
            _check.Click += Check_Click;
        }

        foreach (var name in new[] { "PART_Glyph", "PART_Caption" })
        {
            if (GetTemplateChild(name) is FrameworkElement heading)
            {
                heading.MouseLeftButtonDown += Heading_MouseLeftButtonDown;
                heading.MouseLeftButtonUp += Heading_MouseLeftButtonUp;
            }
        }

        if (_help is not null)
        {
            _help.Click += Help_Click;
            _help.MouseEnter += Help_PointerEntered;
            _help.MouseLeave += Help_PointerLeft;
            _help.GotKeyboardFocus += Help_GotKeyboardFocus;
            _help.LostKeyboardFocus += Help_LostKeyboardFocus;
            _help.PreviewKeyDown += Help_PreviewKeyDown;
        }

        if (_summary?.Child is FrameworkElement summaryContent)
        {
            summaryContent.MouseEnter += Help_PointerEntered;
            summaryContent.MouseLeave += Help_PointerLeft;
        }

        if (_detail is not null)
        {
            _detail.Closed += Detail_Closed;
            if (_detail.Child is FrameworkElement detailContent)
            {
                detailContent.PreviewKeyDown += Detail_PreviewKeyDown;
            }
        }

        if (_detailClose is not null)
        {
            _detailClose.Click += DetailClose_Click;
        }

        SyncParts();
    }

    private void Detach()
    {
        if (_check is not null)
        {
            _check.Click -= Check_Click;
        }

        if (_help is not null)
        {
            _help.Click -= Help_Click;
            _help.MouseEnter -= Help_PointerEntered;
            _help.MouseLeave -= Help_PointerLeft;
            _help.GotKeyboardFocus -= Help_GotKeyboardFocus;
            _help.LostKeyboardFocus -= Help_LostKeyboardFocus;
            _help.PreviewKeyDown -= Help_PreviewKeyDown;
        }

        if (_detail is not null)
        {
            _detail.Closed -= Detail_Closed;
        }

        if (_detailClose is not null)
        {
            _detailClose.Click -= DetailClose_Click;
        }

        _summaryTimer?.Stop();
        _check = null;
        _help = null;
        _summary = null;
        _detail = null;
        _detailClose = null;
    }

    private static void OnCaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BooleanOptionRow)d).SyncParts();

    private static void OnPartTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BooleanOptionRow)d).SyncParts();

    /// <summary>Shows the optional parts that have content and names the help after its option.</summary>
    private void SyncParts()
    {
        if (GetTemplateChild("PART_Glyph") is UIElement glyph)
        {
            glyph.Visibility = Glyph is null ? Visibility.Collapsed : Visibility.Visible;
        }

        if (GetTemplateChild("PART_Explanation") is UIElement explanation)
        {
            explanation.Visibility = string.IsNullOrWhiteSpace(Explanation) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_help is not null)
        {
            var hasHelp = !string.IsNullOrWhiteSpace(HelpSummary) || !string.IsNullOrWhiteSpace(HelpDetail);
            _help.Visibility = hasHelp ? Visibility.Visible : Visibility.Collapsed;
            var name = LocalizationService.Format("OptionHelpName", Caption);
            _help.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, name);
        }
    }

    private void Check_Click(object sender, RoutedEventArgs e) => RaiseToggled();

    private void RaiseToggled() => RaiseEvent(new RoutedEventArgs(ToggledEvent, this));

    // A heading activation is a press and a release on the same element. The capture is what makes a press that began
    // elsewhere - or a touch drag that a scroll viewer took over - fail to arrive as a release here, so a scroll that
    // started on the caption never toggles (BOOLEAN-OPTION section 3 rule 2).
    private void Heading_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsEnabled && sender is UIElement heading && heading.CaptureMouse())
        {
            e.Handled = true;
        }
    }

    private void Heading_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement heading || !heading.IsMouseCaptured)
        {
            return;
        }

        heading.ReleaseMouseCapture();
        e.Handled = true;
        if (IsEnabled && heading is FrameworkElement element && IsPointerInside(element, e))
        {
            Toggle();
        }
    }

    private static bool IsPointerInside(FrameworkElement element, MouseEventArgs e)
    {
        var position = e.GetPosition(element);
        return position.X >= 0 && position.Y >= 0 && position.X <= element.ActualWidth && position.Y <= element.ActualHeight;
    }

    private void Toggle()
    {
        if (_check is null || !IsEnabled)
        {
            return;
        }

        _check.IsChecked = _check.IsChecked != true;
        RaiseToggled();
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        CloseSummary();
        if (_detail is not null && !string.IsNullOrWhiteSpace(HelpDetail))
        {
            _detail.IsOpen = true;
            // A popup is a window of its own: the keyboard has to be sent into it, or Escape and Tab would stay behind.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _detailClose?.Focus());
        }
        else if (_summary is not null && !string.IsNullOrWhiteSpace(HelpSummary))
        {
            _summary.IsOpen = true;
        }

        e.Handled = true;
    }

    private void Help_PointerEntered(object sender, MouseEventArgs e)
    {
        _summaryTimer?.Stop();
        OpenSummary();
    }

    private void Help_PointerLeft(object sender, MouseEventArgs e) => ScheduleSummaryClose();

    private void Help_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => OpenSummary();

    private void Help_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CloseSummary();

    private void Help_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _summary is { IsOpen: true })
        {
            CloseSummary();
            e.Handled = true;
        }
    }

    private void OpenSummary()
    {
        if (_summary is null || _detail is { IsOpen: true } || string.IsNullOrWhiteSpace(HelpSummary))
        {
            return;
        }

        _summary.IsOpen = true;
    }

    private void CloseSummary()
    {
        _summaryTimer?.Stop();
        if (_summary is not null)
        {
            _summary.IsOpen = false;
        }
    }

    private void ScheduleSummaryClose()
    {
        if (_summaryTimer is null)
        {
            _summaryTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = SummaryCloseDelay };
            _summaryTimer.Tick += (_, _) =>
            {
                _summaryTimer.Stop();
                // Keyboard focus keeps it open: a summary shown on focus must outlive a pointer that merely passed by.
                var held = _help is { IsMouseOver: true } or { IsKeyboardFocused: true }
                    || _summary?.Child is FrameworkElement { IsMouseOver: true };
                if (!held)
                {
                    CloseSummary();
                }
            };
        }

        _summaryTimer.Stop();
        _summaryTimer.Start();
    }

    private void Detail_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseDetail();
            e.Handled = true;
        }
    }

    private void DetailClose_Click(object sender, RoutedEventArgs e) => CloseDetail();

    private void CloseDetail()
    {
        if (_detail is { IsOpen: true })
        {
            _returnFocusToHelp = true;
            _detail.IsOpen = false;
        }
    }

    // Closed by Escape or the Close button, the focus returns to the help button (BOOLEAN-OPTION section 4 rule 4),
    // never to the checkbox, which would read as the option being used. Closed by a click elsewhere, the focus is
    // where that click put it and is left alone.
    private void Detail_Closed(object? sender, EventArgs e)
    {
        if (_returnFocusToHelp)
        {
            _returnFocusToHelp = false;
            _help?.Focus();
        }
    }
}
