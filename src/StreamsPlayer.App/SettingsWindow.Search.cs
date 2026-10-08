using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0191: settings search (<c>WINDOWS-UI</c> section 3.5) - matches localized captions and
/// descriptions, each result names the setting and its page and group, and activation by pointer or
/// keyboard navigates to it (section 3.4). Search is navigation only; it never edits a value.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>The search inventory's path to a group: its stable ID and the caption key naming it.</summary>
    private static readonly IReadOnlyDictionary<string, string> GroupCaptionKeys = new Dictionary<string, string>
    {
        ["general.basics"] = "SettingsGeneralBasics",
        ["general.view"] = "SettingsGeneralView",
        ["library.filters"] = "SettingsContentFilters",
        ["library.catalog"] = "StreamCatalogSettings",
        ["library.playlists"] = "PlaylistPortability",
        ["library.tv"] = "TvScheduleTitle",
        ["library.exchange"] = "ExchangeTitle",
        ["playback.general"] = "SettingsPlayback",
        ["playback.video"] = "VideoBackendLabel",
        ["audio.output"] = "AudioOutputDeviceLabel",
        ["audio.channels"] = "AudioChannelModeLabel",
        ["files.frames"] = "FrameFolderLabel",
        ["files.video"] = "VideoRecordingFolderLabel",
        ["files.audio"] = "AudioRecordingFolderLabel",
        ["about.product"] = "ProductName",
        ["about.diagnostics"] = "DiagnosticsHeading"
    };

    /// <summary>
    /// The searchable settings inventory (WINDOWS-UI 3.5): caption key, hint key, page, owning group
    /// and the editor that receives focus when the result is activated. Captions and hints resolve in
    /// the current interface language at search time.
    /// </summary>
    private static readonly IReadOnlyList<(int Page, string GroupId, string CaptionKey, string HintKey, string TargetName)> SearchInventory =
    [
        (0, "general.basics", "Language", "MachineTranslationNotice", "LanguageBox"),
        (0, "general.basics", "ThemeLabel", "ThemeHint", "ThemeBox"),
        (0, "general.view", "StreamTileSizeLabel", "TileSizeHint", "TileSizeBox"),
        (0, "general.view", "AnimatedBackdrop", "AnimatedBackdropHint", "AnimatedBackdropCheckBox"),
        (1, "library.filters", "HideAdultContent", "HideAdultContentHint", "HideAdultContentCheckBox"),
        (1, "library.filters", "UpdateStreamPreviews", "UpdateStreamPreviewsHint", "UpdatePreviewsCheckBox"),
        (1, "library.filters", "ManageHiddenOpen", "ManageHiddenTip", "ManageHiddenButton"),
        (1, "library.catalog", "ImportCatalogFromFile", "ImportCatalogFromFileTip", "ImportCatalogFromFileButton"),
        (1, "library.catalog", "CatalogSnapshotSettings", "CatalogSnapshotSettingsTip", "ApplyCatalogSnapshotButton"),
        (1, "library.catalog", "DeleteDownloaded", "DeleteDownloadedTip", "DeleteDownloadedButton"),
        (1, "library.catalog", "DeleteImportedCatalog", "DeleteImportedCatalogTip", "DeleteImportedCatalogButton"),
        (1, "library.playlists", "ImportFromFile", "ImportListTip", "ImportFromFileButton"),
        (1, "library.playlists", "ImportFromUrl", "ImportListTip", "ImportFromUrlButton"),
        (1, "library.playlists", "ExportAll", "ExportListTip", "ExportAllButton"),
        (1, "library.playlists", "ExportPinned", "ExportListTip", "ExportPinnedButton"),
        (1, "library.tv", "TvScheduleAddressName", "TvScheduleHint", "TvScheduleAddressBox"),
        (1, "library.exchange", "ExchangeTitle", "ExchangeHint", "ExchangeAddressBox"),
        (2, "playback.general", "KeepAwakeDuringPlayback", "KeepAwakeHint", "KeepAwakeCheckBox"),
        (2, "playback.general", "SystemMediaControls", "SmtcHint", "SystemMediaControlsCheckBox"),
        (2, "playback.general", "ResumePlaybackOnStartup", "ResumePlaybackHint", "ResumePlaybackCheckBox"),
        (2, "playback.video", "VideoBackendLabel", "VideoBackendHint", "VideoBackendBox"),
        (3, "audio.output", "AudioOutputDeviceLabel", "AudioOutputDeviceHint", "AudioDeviceBox"),
        (3, "audio.channels", "AudioChannelModeLabel", "AudioChannelModeHint", "AudioChannelBox"),
        (4, "files.frames", "FrameFolderLabel", "FrameFolderHint", "FrameFolderBox"),
        (4, "files.video", "VideoRecordingFolderLabel", "VideoRecordingFolderHint", "VideoRecordingFolderBox"),
        (4, "files.audio", "AudioRecordingFolderLabel", "AudioRecordingFolderHint", "AudioRecordingFolderBox"),
        (5, "about.diagnostics", "SendLogs", "SendLogsTip", "SendLogsButton")
    ];

    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SettingsSearchBox.Text.Trim();
        SearchResultsList.Items.Clear();
        if (query.Length == 0)
        {
            CloseSearchResults();
            return;
        }

        foreach (var (page, groupId, captionKey, hintKey, targetName) in SearchInventory)
        {
            // A setting this installation does not show (the bundled snapshot's command when the
            // build carries none) is not offered: a result must lead somewhere.
            if (PageAt(page).FindName(targetName) is not FrameworkElement { Visibility: Visibility.Visible })
            {
                continue;
            }

            var caption = LocalizationService.Get(captionKey);
            var hint = LocalizationService.Get(hintKey);
            if (!caption.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && !hint.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            var path = $"{LocalizationService.Get(PageNameKeys[page])}  ·  {LocalizationService.Get(GroupCaptionKeys[groupId])}";
            var item = new ListBoxItem { Tag = (page, groupId, targetName) };
            System.Windows.Automation.AutomationProperties.SetName(item, $"{caption}, {path}");
            var stack = new StackPanel { Margin = new Thickness(4, 3, 4, 3) };
            var captionText = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
            var pathText = new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
            // A DynamicResource reference, not a copied brush: the results must follow a theme switch
            // like every other caption (APP-STYLE section 3).
            pathText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            stack.Children.Add(captionText);
            stack.Children.Add(pathText);
            item.Content = stack;
            SearchResultsList.Items.Add(item);
        }

        if (SearchResultsList.Items.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = LocalizationService.Get("SettingsSearchNoResults"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 3, 4, 3)
            };
            // A DynamicResource reference, not a copied brush: the empty result follows the theme.
            emptyText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            SearchResultsList.Items.Add(new ListBoxItem { IsEnabled = false, Content = emptyText });
        }

        SearchResultsPopup.IsOpen = true;
    }

    private ListBoxItem? FirstActiveResult() =>
        SearchResultsPopup.IsOpen
            ? SearchResultsList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.IsEnabled)
            : null;

    /// <summary>
    /// Previewed, so the text box's own line-navigation bindings cannot swallow Down first. Escape
    /// leaves a search in progress before it can reach the window; with nothing typed it passes
    /// through and closes the window as everywhere else (APP-BEHAVIOUR rule 1).
    /// </summary>
    private void SettingsSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (SettingsSearchBox.Text.Length > 0 || SearchResultsPopup.IsOpen))
        {
            CloseSearchResults();
            SettingsSearchBox.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && FirstActiveResult() is { } first)
        {
            ActivateSearchResult(first);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && FirstActiveResult() is { } top)
        {
            // Into the result list, where the arrows choose and Enter activates.
            SearchResultsList.SelectedItem = top;
            top.Focus();
            e.Handled = true;
        }
    }

    private void SearchResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchResultsList.SelectedItem is ListBoxItem { IsEnabled: true } item)
        {
            ActivateSearchResult(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearchResults();
            SettingsSearchBox.Focus();
            e.Handled = true;
        }
    }

    private void SearchResultsList_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(SearchResultsList, source) is ListBoxItem { IsEnabled: true } item)
        {
            ActivateSearchResult(item);
        }
    }

    private void ActivateSearchResult(ListBoxItem item)
    {
        if (item.Tag is not (int page, string groupId, string targetName))
        {
            return;
        }

        CloseSearchResults();
        SettingsSearchBox.Clear();

        // Explicit navigation to a setting selects its page, expands the owning group, scrolls it into
        // view and gives the editor focus (WINDOWS-UI 3.4) - never firing change handlers as edits.
        SelectPage(page);
        var group = FindGroup(groupId);
        if (group is not null)
        {
            group.IsExpanded = true;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (group is not null)
            {
                PageScroll.ScrollToVerticalOffset(Math.Max(0, ScrollOffsetOf(group) - 8));
            }

            if (PageAt(page).FindName(targetName) is FrameworkElement editor)
            {
                editor.Focus();
            }
        });
    }

    private void CloseSearchResults() => SearchResultsPopup.IsOpen = false;
}
