using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace StreamsPlayer.App;

// SP-0050: the header's rare and mode-conditional actions live in one named menu. Nothing here
// re-implements a behaviour - every entry is wired to the handler its former header button used.
public partial class MainWindow
{
    private void OperationsButton_Click(object sender, RoutedEventArgs e)
    {
        if (OperationsButton.ContextMenu is not { } menu)
        {
            return;
        }

        BuildOperationsMenu(menu);
        menu.PlacementTarget = OperationsButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Rebuilt on every open rather than mounted once and kept in sync. That is what lets the
    /// always-on-top check mark read the live window state and the preview-refresh entry appear only in
    /// grid mode, without a single subscription to keep correct.
    /// </summary>
    private void BuildOperationsMenu(ContextMenu menu)
    {
        menu.Items.Clear();

        var alwaysOnTop = new MenuItem
        {
            Header = LocalizationService.Get("AlwaysOnTop"),
            IsCheckable = true,
            IsChecked = Topmost,
            ToolTip = LocalizationService.Get("MainAlwaysOnTopName")
        };
        AutomationProperties.SetName(alwaysOnTop, LocalizationService.Get("MainAlwaysOnTopName"));
        alwaysOnTop.Click += AlwaysOnTopMenuItem_Click;
        menu.Items.Add(alwaysOnTop);

        if (CanRefreshPreviews)
        {
            menu.Items.Add(BuildEntry("RefreshPreviews", "RefreshPreviewsTip", "RefreshPreviewsName", RefreshPreviewsButton_Click));
        }

        menu.Items.Add(BuildEntry("HistoryOpen", "HistoryTip", "HistoryTip", HistoryButton_Click));
        menu.Items.Add(BuildEntry("AddStreamPlain", "AddStreamTip", "AddStreamTip", AddButton_Click));
        menu.Items.Add(BuildEntry("PasteChannelPlain", "PasteChannelTip", "PasteChannelTip", PasteChannelButton_Click));

        // The catalog refresh keeps the emphasis it had as a header button: last, fenced off, and the
        // only entry allowed to carry the accent. It is the one action a first-time user is looking for.
        menu.Items.Add(new Separator());
        var refresh = BuildEntry("UpdateCatalogPlain", "UpdateCatalogTip", "UpdateCatalogTip", RefreshButton_Click);
        refresh.SetResourceReference(FrameworkElement.StyleProperty, "AccentMenuItem");
        menu.Items.Add(refresh);
    }

    private static MenuItem BuildEntry(string headerKey, string tooltipKey, string nameKey, RoutedEventHandler handler)
    {
        var item = new MenuItem
        {
            Header = LocalizationService.Get(headerKey),
            ToolTip = LocalizationService.Get(tooltipKey)
        };
        AutomationProperties.SetName(item, LocalizationService.Get(nameKey));
        item.Click += handler;
        return item;
    }

    /// <summary>
    /// The markup guard the header checkbox carried - disabled until the state load finished - is gone
    /// with the checkbox, so the guard has to live here. A click before the load must not write a
    /// preference derived from an empty state, and the entry must not be left showing a state the window
    /// does not have.
    /// </summary>
    private async void AlwaysOnTopMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }

        if (!_preferencesLoaded)
        {
            item.IsChecked = Topmost;
            return;
        }

        Topmost = item.IsChecked;
        _state = await PersistAsync(_state with { MainWindowTopmost = Topmost });
    }
}
