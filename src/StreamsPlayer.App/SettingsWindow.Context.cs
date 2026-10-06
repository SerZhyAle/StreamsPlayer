using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0191: the settings window's private navigation context (<c>WINDOWS-UI</c> sections 2.3 and 3.1-3.4,
/// <c>APP-BEHAVIOUR</c> rule 10) - page switching, the collapsible groups, the per-page viewport
/// anchor and the remembered window rectangle. Everything here is navigation state: none of it runs an
/// edit handler, and all of it is saved to <c>settings-ui.json</c>, never to the catalog document.
/// </summary>
public partial class SettingsWindow
{
    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e) => RestoreViewportSoon();

    private void Group_ExpandedChanged(object sender, RoutedEventArgs e)
    {
        if (_restoringContext || sender is not Expander expander)
        {
            return;
        }

        var id = SettingsUiProperties.GetGroupId(expander);
        if (!string.IsNullOrEmpty(id))
        {
            _uiContext.Groups[id] = expander.IsExpanded;
        }
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _restoringContext || _syncingNav || NavList.SelectedIndex < 0)
        {
            return;
        }

        RememberViewport();
        _currentPageIndex = NavList.SelectedIndex;
        SyncHeader();
        ShowPageOnly(_currentPageIndex);
        CloseSearchResults();
        RestoreViewportSoon();
    }

    /// <summary>Selects a page from code (a search result) without the list's handler re-entering.</summary>
    private void SelectPage(int page)
    {
        RememberViewport();
        _syncingNav = true;
        NavList.SelectedIndex = page;
        _syncingNav = false;
        _currentPageIndex = page;
        SyncHeader();
        ShowPageOnly(page);
    }

    private void SyncHeader()
    {
        PageTitleText.Text = LocalizationService.Get(PageNameKeys[_currentPageIndex]);
        PageDescText.Text = LocalizationService.Get(PageDescKeys[_currentPageIndex]);
    }

    private FrameworkElement PageAt(int index) => index switch
    {
        1 => PageLibrary,
        2 => PagePlayback,
        3 => PageAudio,
        4 => PageFiles,
        5 => PageAbout,
        _ => PageGeneral
    };

    private void ShowPageOnly(int index)
    {
        for (var page = 0; page < PageNameKeys.Length; page++)
        {
            PageAt(page).Visibility = page == index ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SettingsExpandAll_Click(object sender, RoutedEventArgs e) => SetAllGroupsExpanded(expanded: true);

    private void SettingsCollapseAll_Click(object sender, RoutedEventArgs e) => SetAllGroupsExpanded(expanded: false);

    private void SetAllGroupsExpanded(bool expanded)
    {
        foreach (var expander in VisiblePageExpanders())
        {
            if (expander.IsExpanded == expanded)
            {
                continue;
            }

            if (!expanded)
            {
                // WINDOWS-UI 3.2: if the keyboard focus is inside a group that is about to hide, the
                // focus moves to the header before the children leave the traversal.
                if (Keyboard.FocusedElement is DependencyObject focused && expander.IsAncestorOf(focused)
                    && expander.Template.FindName("HeaderSite", expander) is ToggleButton header)
                {
                    header.Focus();
                }
            }

            expander.IsExpanded = expanded;
        }
    }

    private IEnumerable<Expander> AllExpanders() =>
        FindVisualDescendants<Expander>(SettingsContent);

    private IEnumerable<Expander> VisiblePageExpanders() =>
        FindVisualDescendants<Expander>(PageAt(_currentPageIndex));

    private Expander? FindGroup(string groupId) =>
        VisiblePageExpanders().FirstOrDefault(expander =>
            string.Equals(SettingsUiProperties.GetGroupId(expander), groupId, StringComparison.Ordinal));

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static string ViewportKey(int pageIndex) => $"page{pageIndex}";

    /// <summary>
    /// The element's top as an absolute offset into the scrolled page. A bound read relative to the
    /// scroll viewer is relative to the current viewport, so the current offset is added back - the
    /// one scroll viewer hosts every page, and switching pages does not reset it.
    /// </summary>
    private double ScrollOffsetOf(FrameworkElement element) =>
        PageScroll.VerticalOffset + element.TransformToVisual(PageScroll).TransformBounds(new Rect(element.RenderSize)).Top;

    /// <summary>Records the current page's viewport as its topmost visible group and the offset into it.</summary>
    private void RememberViewport()
    {
        foreach (var expander in VisiblePageExpanders())
        {
            var bounds = expander.TransformToVisual(PageScroll).TransformBounds(new Rect(expander.RenderSize));
            if (bounds.Bottom <= 0)
            {
                continue;
            }

            if (SettingsUiProperties.GetGroupId(expander) is { Length: > 0 } groupId)
            {
                _uiContext.Viewports[ViewportKey(_currentPageIndex)] = new SettingsViewportAnchor(groupId, Math.Max(0, -bounds.Top));
                return;
            }

            break;
        }

        _uiContext.Viewports.Remove(ViewportKey(_currentPageIndex));
    }

    private void RestoreViewportSoon()
    {
        // After layout: the anchor's offset means nothing until the page has been measured, and the
        // restore must not fire an edit handler - scrolling touches navigation only.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RestoreViewport);
    }

    /// <summary>
    /// Brings the current page's remembered anchor back to the top - from this session or, for a fresh
    /// window, from the file (WINDOWS-UI 3.3). An ID the markup no longer has falls back to the page top.
    /// </summary>
    private void RestoreViewport()
    {
        if (_uiContext.Viewports.TryGetValue(ViewportKey(_currentPageIndex), out var anchor)
            && FindGroup(anchor.GroupId) is { } target)
        {
            var within = Math.Clamp(anchor.OffsetWithinGroup, 0, Math.Max(0, target.ActualHeight));
            PageScroll.ScrollToVerticalOffset(ScrollOffsetOf(target) + within);
            return;
        }

        PageScroll.ScrollToTop();
    }

    /// <summary>
    /// The second placement pass, once the window has a DPI transform of its own (APP-BEHAVIOUR
    /// rule 10): a rectangle remembered at one scale is fitted to the work area at the current one,
    /// and a monitor that is gone has already been exchanged for the nearest surviving one.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_placedFromMemory)
        {
            var wanted = new ScreenRect(Left, Top, Width, Height);
            var area = MonitorWorkArea.Around(wanted, this);
            var fit = ScreenPlacement.Fit(wanted, area, MinWidth, MinHeight);
            Left = fit.Left;
            Top = fit.Top;
            Width = fit.Width;
            Height = fit.Height;
        }
    }

    /// <summary>
    /// Writes the window's private navigation context on the way out, once: the last page, every
    /// group's expansion, the current page's viewport anchor and - when this close leaves a normal
    /// rectangle to remember - the window rectangle. A maximized close deliberately writes no
    /// rectangle, so the remembered normal one survives (APP-BEHAVIOUR rule 10). Fire and forget:
    /// the write must not hold up a window the user has closed, and its failure costs one memory.
    /// </summary>
    /// <remarks>
    /// OnClosing also runs when the application is shutting down around this window, when the visual
    /// tree the viewport anchor reads may already be coming apart. The context is a convenience -
    /// navigation state, never user data - so a save that cannot be computed is dropped with a log
    /// line rather than allowed to turn a close into a crash.
    /// </remarks>
    private void SaveNavigationContext()
    {
        try
        {
            RememberViewport();
            _uiContext = _uiContext with { LastPageIndex = _currentPageIndex };

            if (WindowState == WindowState.Normal)
            {
                var rectangle = MonitorWorkArea.Placement(this);
                if (rectangle.Width >= MinWidth && rectangle.Height >= MinHeight)
                {
                    _uiContext = _uiContext with { Window = new SettingsWindowRectangle(rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height) };
                }
            }

            _ = _uiContextStore.SaveAsync(_uiContext);
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(SaveNavigationContext), exception);
        }
    }
}
