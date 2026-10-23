using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private bool _headerLayoutReady;
    private bool _updatingHeaderLayout;
    private bool _compactHeader;
    private bool _compactFiltersExpanded;
    private DispatcherOperation? _headerLayoutOperation;

    private void InitializeHeaderLayout()
    {
        _headerLayoutReady = true;
        CompactFiltersButton.ContentTemplate = HeaderContentTemplate("GlyphOnlyButton");
        UiProfileService.Changed += ScheduleHeaderLayout;
        Closed += (_, _) =>
        {
            _headerLayoutReady = false;
            UiProfileService.Changed -= ScheduleHeaderLayout;
            _headerLayoutOperation?.Abort();
        };
        ScheduleHeaderLayout();
    }

    private DataTemplate HeaderContentTemplate(string styleKey) =>
        (DataTemplate)((Style)FindResource(styleKey)).Setters.OfType<Setter>()
            .Single(setter => setter.Property == ContentControl.ContentTemplateProperty).Value;

    private void HeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            ScheduleHeaderLayout();
        }
    }

    private void HeaderAction_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => ScheduleHeaderLayout();

    private void ScheduleHeaderLayout()
    {
        if (!_headerLayoutReady || _updatingHeaderLayout || Dispatcher.HasShutdownStarted
            || _headerLayoutOperation?.Status == DispatcherOperationStatus.Pending)
        {
            return;
        }

        _headerLayoutOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_headerLayoutReady && !_shuttingDown)
            {
                UpdateHeaderLayout();
            }
        });
    }

    private void UpdateHeaderLayout()
    {
        if (HeaderGrid.ActualWidth <= 0)
        {
            return;
        }

        _updatingHeaderLayout = true;
        try
        {
            // Measure the full presentation each time; measuring the compact one would make the
            // breakpoint alternate as captions disappear and reappear. Nothing renders between passes.
            var hadSearchFocus = SearchTools.IsKeyboardFocusWithin;
            SearchTools.Visibility = Visibility.Visible;
            SearchTools.Margin = new Thickness(0);
            FiltersButton.Visibility = Visibility.Visible;
            var natural = new Size(double.PositiveInfinity, double.PositiveInfinity);
            var actionsWidth = HeaderActions.Margin.Left + HeaderActions.Margin.Right;
            foreach (UIElement action in HeaderActions.Children)
            {
                if (action == SettingsButton || action == OperationsButton)
                {
                    actionsWidth += CaptionedActionWidth((Button)action);
                }
                else
                {
                    action.Measure(natural);
                    actionsWidth += action.DesiredSize.Width;
                }
            }
            SearchTools.InvalidateMeasure();
            SearchTools.Measure(natural);
            var compact = SearchTools.DesiredSize.Width + actionsWidth > HeaderGrid.ActualWidth;
            if (compact && !_compactHeader)
            {
                _compactFiltersExpanded = hadSearchFocus;
            }

            _compactHeader = compact;
            if (compact)
            {
                var template = HeaderContentTemplate("GlyphOnlyButton");
                SettingsButton.ContentTemplate = template;
                OperationsButton.ContentTemplate = template;
            }
            else
            {
                SettingsButton.ClearValue(ContentControl.ContentTemplateProperty);
                OperationsButton.ClearValue(ContentControl.ContentTemplateProperty);
            }

            CompactFiltersButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRow(SearchTools, compact ? 1 : 0);
            Grid.SetColumnSpan(SearchTools, compact ? 2 : 1);
            SearchTools.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0);
            UpdateFilterPanelChrome();
        }
        finally
        {
            _updatingHeaderLayout = false;
        }
    }

    private static double CaptionedActionWidth(Button source)
    {
        // A fresh captioned control measures independently of the displayed button's recycled
        // icon-only template, whose replacement takes effect on a later layout pass.
        var probe = new Button
        {
            Style = source.Style,
            Content = source.Content,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            FontWeight = source.FontWeight,
            FontStyle = source.FontStyle,
            FontStretch = source.FontStretch,
            FlowDirection = source.FlowDirection,
            Padding = source.Padding,
            Margin = source.Margin,
            BorderThickness = source.BorderThickness,
            MinWidth = source.MinWidth,
            MinHeight = source.MinHeight
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private void CompactFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        _compactFiltersExpanded = !_compactFiltersExpanded;
        UpdateFilterPanelChrome();
        if (_compactFiltersExpanded)
        {
            SearchBox.Focus();
        }
        else
        {
            CompactFiltersButton.Focus();
        }
    }

    private void SearchTools_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_compactHeader && _compactFiltersExpanded && e.Key == Key.Escape)
        {
            _compactFiltersExpanded = false;
            UpdateFilterPanelChrome();
            CompactFiltersButton.Focus();
            e.Handled = true;
        }
    }
}
