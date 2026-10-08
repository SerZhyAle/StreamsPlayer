using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0109 / SP-0188: <c>APP-SETTINGS</c> and <c>APP-BEHAVIOUR</c> conformance rules for the unified
/// settings surface. Values apply on touch; irreversible operations sit behind explicit buttons with
/// confirmations; Close is the sole exit and is mapped to Escape.
/// </summary>
public sealed class DesktopUxConformanceTests
{
    /// <summary>
    /// Every event handler the Settings markup may name, and why each one is allowed. A new handler
    /// fails this gate until it is added here with its reason.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SettingsHandlers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["LanguageBox_SelectionChanged"] = "applies the language choice on touch",
        ["ExchangeEnroll_Click"] = "explicitly enrolls with a one-use proof and approved certificate",
        ["ExchangeEnabled_Click"] = "enables or disables the enrolled source on touch",
        ["ExchangeAutoAcceptCasts_Click"] = "applies the auto-accept cast setting on touch",
        ["ExchangeTrust_Click"] = "explicitly confirms a certificate replacement",
        ["ExchangeForget_Click"] = "explicitly removes the account after confirmation",
        ["ThemeBox_SelectionChanged"] = "applies the theme choice on touch",
        ["TileSizeBox_SelectionChanged"] = "applies the tile size on touch",
        ["AnimatedBackdropCheckBox_Click"] = "applies the animated backdrop setting on touch",
        ["HideAdultContentCheckBox_Click"] = "applies the adult content filter on touch",
        ["UpdatePreviewsCheckBox_Click"] = "applies the thumbnail preview update setting on touch",
        ["KeepAwakeCheckBox_Click"] = "applies the keep awake setting on touch",
        ["SystemMediaControlsCheckBox_Click"] = "applies the system media controls setting on touch",
        ["ResumePlaybackCheckBox_Click"] = "applies the resume playback setting on touch",
        ["VideoBackendBox_SelectionChanged"] = "applies the video backend choice on touch",
        ["VideoComponentsInstall_Click"] = "operation button to install FlyleafLib components",
        ["VideoComponentsRemove_Click"] = "operation button to remove FlyleafLib components",
        ["FrameFolderBrowse_Click"] = "edits the capture folder",
        ["FrameFolderReset_Click"] = "resets the capture folder",
        ["FrameFolderOpen_Click"] = "opens the folder in Explorer and changes nothing",
        ["ImportCatalogFromFile_Click"] = "operation button to import catalog archive",
        ["ApplyCatalogSnapshot_Click"] = "operation button to apply bundled catalog snapshot",
        ["DeleteDownloaded_Click"] = "operation button with confirmation to delete downloaded catalog streams",
        ["DeleteImportedCatalog_Click"] = "operation button with confirmation to delete imported catalog streams",
        ["ImportFromFile_Click"] = "operation button to import playlist from file",
        ["ImportFromUrl_Click"] = "operation button to import playlist from URL",
        ["ExportAll_Click"] = "operation button to export all user channels",
        ["ExportPinned_Click"] = "operation button to export pinned channels",
        ["ManageHidden_Click"] = "operation button to manage hidden channels",
        ["TvScheduleDownload_Click"] = "operation button with confirmation to download TV schedule",
        ["TvScheduleRemove_Click"] = "operation button with confirmation to remove TV schedule",
        ["SendLogs_Click"] = "operation button for diagnostic log report",
        ["OpenLink_Click"] = "opens a web page and changes nothing",
        ["VideoComponentsCancel_Click"] = "cancels ongoing components download",
        ["AudioDeviceBox_SelectionChanged"] = "applies the audio output device choice on touch",
        ["AudioChannelBox_SelectionChanged"] = "applies the audio channel mode on touch",
        ["NavList_SelectionChanged"] = "switches the visible settings page; private navigation context, commits nothing (SP-0191)",
        ["SettingsSearch_TextChanged"] = "filters the settings search inventory; private navigation, commits nothing (SP-0191, WINDOWS-UI 3.5)",
        ["SettingsSearchBox_KeyDown"] = "lets Escape leave the search before the window and Enter activate a result (SP-0191)",
        ["SearchResultsList_KeyDown"] = "keyboard activation of a search result (Enter/Space) and Escape back to the search box (SP-0191, WINDOWS-UI 3.4/3.5)",
        ["SearchResultsList_Click"] ="navigates to the chosen setting: page, expanded group, scrolled and focused editor (SP-0191, WINDOWS-UI 3.4)",
        ["SettingsExpandAll_Click"] = "changes group visibility only; commits nothing (SP-0191, WINDOWS-UI 2.3)",
        ["SettingsCollapseAll_Click"] = "changes group visibility only; commits nothing (SP-0191, WINDOWS-UI 2.3)",
        ["Window_KeyDown"] = "closes the settings window on Escape key"
    };

    private static readonly HashSet<string> EventAttributes = new(StringComparer.Ordinal)
    {
        "Click", "Checked", "Unchecked", "SelectionChanged", "TextChanged", "ValueChanged",
        "MouseDoubleClick", "MouseLeftButtonUp", "PreviewMouseLeftButtonUp", "KeyDown", "PreviewKeyDown",
        "LostFocus", "DropDownClosed"
    };

    private static readonly HashSet<string> ValueChangingEvents = new(StringComparer.Ordinal)
    {
        "SelectionChanged", "TextChanged", "ValueChanged", "Checked", "Unchecked"
    };

    private static readonly HashSet<string> IrreversibleHandlers = new(StringComparer.Ordinal)
    {
        "DeleteDownloaded_Click",
        "DeleteImportedCatalog_Click",
        "TvScheduleRemove_Click",
        "ExchangeForget_Click"
    };

    [Fact]
    public void TheSettingsWindowNamesOnlyDeclaredHandlersAndAttachesNoIrreversibleActionToValueControls()
    {
        var markup = SettingsMarkup();
        var problems = new List<string>();
        var handlers = 0;

        foreach (var element in markup.Descendants())
        {
            foreach (var attribute in element.Attributes().Where(attribute => EventAttributes.Contains(attribute.Name.LocalName)))
            {
                handlers++;
                if (!SettingsHandlers.ContainsKey(attribute.Value))
                {
                    problems.Add(
                        $"SettingsWindow.xaml:{((IXmlLineInfo)attribute).LineNumber}: {attribute.Name.LocalName}=\"{attribute.Value}\" " +
                        "is not on the list of declared settings handlers.");
                }

                if (ValueChangingEvents.Contains(attribute.Name.LocalName) && IrreversibleHandlers.Contains(attribute.Value))
                {
                    problems.Add(
                        $"SettingsWindow.xaml:{((IXmlLineInfo)attribute).LineNumber}: {attribute.Name.LocalName}=\"{attribute.Value}\" " +
                        "attaches an irreversible operation to a value control. Irreversible actions must be explicit buttons with confirmations.");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(handlers >= SettingsHandlers.Count, $"Only {handlers} handlers were found in the Settings markup.");
    }

    [Fact]
    public void TheSettingsWindowMapsEscapeAndHasNoSaveOrCancel()
    {
        var markup = SettingsMarkup();
        // APP-BEHAVIOUR rule 1 / APP-SETTINGS rule 4: Escape is mapped to close the window.
        Assert.Equal("Window_KeyDown", markup.Root?.Attribute("KeyDown")?.Value);

        // SP-0188: Save and Cancel are eliminated; all reversible settings apply immediately on touch.
        Assert.DoesNotContain(markup.Descendants(), element => element.Attribute("Click")?.Value == "Save_Click");
        Assert.DoesNotContain(markup.Descendants(), element => element.Attribute("Click")?.Value == "Cancel_Click");
    }

    /// <summary>
    /// SP-0191: <c>APP-SETTINGS</c> rule 2, read statically - About is the last destination of the
    /// settings navigation, in the same place in every language. The check reads the declared
    /// automation names, which are resource references, so it cannot drift with a retranslation.
    /// The navigation is a list box since the WINDOWS-UI rework; the declaration is what matters,
    /// never the control class.
    /// </summary>
    [Fact]
    public void TheSettingsNavigationEndsWithAbout()
    {
        var markup = SettingsMarkup();
        var pages = markup.Descendants()
            .Where(element => element.Name.LocalName == "ListBoxItem")
            .Select(element => element.Attribute("AutomationProperties.Name")?.Value)
            .ToList();

        Assert.True(pages.Count >= 2, "The settings navigation has no pages.");
        Assert.Equal("{DynamicResource SettingsAbout}", pages.Last());
        Assert.Equal(pages.Count, pages.Distinct().Count());
    }

    /// <summary>
    /// SP-0211: <c>APP-SETTINGS</c> rule 7 and <c>ICON-RENDER</c> section 3 rule 5, read statically - the
    /// settings window sets the 28 px pointer floor on the controls whose painted part is smaller (the check
    /// box, the combo box, a navigation row) and carries no inline <c>Hyperlink</c>, whose target is one line
    /// of text. A link that opens a page is a button with the floor. The driven run
    /// (<c>temp/SP-0211/measure.ps1</c>) is what measures the result; this keeps it from being undone.
    /// </summary>
    [Fact]
    public void TheSettingsWindowSetsThePointerHitTargetFloorAndHasNoInlineLinks()
    {
        var markup = SettingsMarkup();
        const string floor = "28";

        bool SetsFloor(Func<XElement, bool> owner) => markup.Descendants()
            .Where(owner)
            .SelectMany(element => element.Descendants())
            .Any(setter => setter.Name.LocalName == "Setter"
                && setter.Attribute("Property")?.Value == "MinHeight"
                && setter.Attribute("Value")?.Value == floor);

        Assert.True(SetsFloor(style => style.Name.LocalName == "Style" && style.Attribute("TargetType")?.Value == "CheckBox"),
            "The settings window's CheckBox style does not set MinHeight 28.");
        Assert.True(SetsFloor(style => style.Name.LocalName == "Style" && style.Attribute("TargetType")?.Value == "ComboBox"),
            "The settings window's ComboBox style does not set MinHeight 28.");
        Assert.True(SetsFloor(style => style.Name.LocalName == "Style" && style.Attribute("TargetType")?.Value == "ListBoxItem"),
            "The navigation row style does not set MinHeight 28.");
        Assert.True(SetsFloor(style => style.Name.LocalName == "Style" && style.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "SettingsLinkButton"),
            "The settings link button does not set MinHeight 28.");
        Assert.DoesNotContain(markup.Descendants(), element => element.Name.LocalName == "Hyperlink");
    }

    /// <summary>
    /// SP-0191: every collapsible settings group carries a stable internal ID (<c>WINDOWS-UI</c>
    /// section 3.3) - the key the group's expansion and the page viewport are remembered by. A group
    /// without one cannot be restored, and a duplicate would make two groups share one memory.
    /// </summary>
    [Fact]
    public void EverySettingsGroupHasAStableUniqueId()
    {
        var markup = SettingsMarkup();
        var ids = markup.Descendants()
            .Where(element => element.Name.LocalName == "Expander")
            .Select(element => element.Attributes()
                .Single(attribute => attribute.Name.LocalName == "SettingsUiProperties.GroupId").Value)
            .ToList();

        Assert.NotEmpty(ids);
        Assert.DoesNotContain(ids, string.IsNullOrEmpty);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    /// <summary>
    /// SP-0191: a value row (<c>WINDOWS-UI</c> section 4) is a two-column grid - caption and hint in
    /// the leading column, the editor in the trailing one. A child left without a column attribute
    /// lands in column 0 and draws over the caption, which no compiler or binding error reports; the
    /// first audit found eight editors placed that way.
    /// </summary>
    [Fact]
    public void EveryValueRowPutsItsEditorInTheTrailingColumn()
    {
        var markup = SettingsMarkup();
        var rows = markup.Descendants()
            .Where(element => element.Name.LocalName == "Grid"
                && element.Attribute("Style")?.Value == "{StaticResource ValueRow}")
            .ToList();
        var problems = new List<string>();

        foreach (var row in rows)
        {
            var children = row.Elements().Where(element => !element.Name.LocalName.StartsWith("Grid.", StringComparison.Ordinal)).ToList();
            if (children.Count != 2)
            {
                problems.Add($"SettingsWindow.xaml:{((IXmlLineInfo)row).LineNumber}: a value row holds {children.Count} children, not caption + editor.");
                continue;
            }

            if (children[0].Attribute("Grid.Column") is { } leading && leading.Value != "0")
            {
                problems.Add($"SettingsWindow.xaml:{((IXmlLineInfo)children[0]).LineNumber}: the caption is not in the leading column.");
            }

            if (children[1].Attribute("Grid.Column")?.Value != "1")
            {
                problems.Add($"SettingsWindow.xaml:{((IXmlLineInfo)children[1]).LineNumber}: the editor is not in the trailing column (Grid.Column=\"1\").");
            }
        }

        Assert.NotEmpty(rows);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void NoExceptionTextReachesTheUser()
    {
        var problems = new List<string>();
        var found = 0;

        foreach (var file in AppSourceFile.LoadAll("*.cs"))
        {
            found += MessageLeaks(file, problems);
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        // A scan that matched nothing would pass loudest exactly when it had stopped working. The floor is
        // the log calls that carry an exception's text today.
        Assert.True(found >= 10, $"Only {found} exception-text uses were found in the application sources.");
    }

    [Fact]
    public void TheLeakScanCatchesAMessageBoxAndPassesAMultiLineLogCall()
    {
        const string source = """
            class Sample
            {
                void Leak(Exception exception)
                {
                    MessageBox.Show(owner,
                        exception.Message, title);
                }

                void Logged(Exception ex)
                {
                    // exception.Message in a comment is not a use
                    log.Event(
                        "FALLBACK",
                        $"what={what}",
                        $"err={ex.Message}");
                    var key = ex.Message.Contains("CSV") ? "a" : "b";
                    SetStatus(Format("Failed", $"{ex.Message}"));
                }
            }
            """;
        var problems = new List<string>();

        var found = MessageLeaks(AppSourceFile.Parse("Sample.cs", source), problems);

        Assert.Equal(4, found);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, problem => problem.StartsWith("Sample.cs:6:", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.StartsWith("Sample.cs:17:", StringComparison.Ordinal));
    }

    private static readonly Regex MessageUse = new(@"\b\w+\??\.Message\b", RegexOptions.Compiled);

    // A logging call opens the statement: `_log.Event(`, `log.Error(`, `_logNoise.Observe(`.
    private static readonly Regex LogCall = new(@"\b_?log\w*\.\w+\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Reports every use of an exception's <c>Message</c> that is neither inside a logging statement nor a
    /// <c>.Contains(</c> test that only chooses a localized key.
    /// </summary>
    /// <remarks>
    /// Matched on the raw text, because an interpolation hole is code but the masked text blanks it with the
    /// rest of the string. The statement is found on the masked text, where a brace inside a string can no
    /// longer pose as a block boundary, and a match whose line is a comment is skipped.
    /// </remarks>
    private static int MessageLeaks(AppSourceFile file, List<string> problems)
    {
        var found = 0;
        foreach (Match match in MessageUse.Matches(file.Text))
        {
            if (IsInCommentLine(file.Text, match.Index))
            {
                continue;
            }

            found++;
            var after = file.Text.AsSpan(match.Index + match.Length);
            if (after.StartsWith(".Contains(", StringComparison.Ordinal) ||
                after.StartsWith("?.Contains(", StringComparison.Ordinal))
            {
                continue;
            }

            var start = file.Masked.LastIndexOfAny([';', '{', '}'], match.Index) + 1;
            if (LogCall.IsMatch(file.Masked[start..match.Index]))
            {
                continue;
            }

            problems.Add(
                $"{file.Name}:{file.LineAt(match.Index)}: {match.Value} is used outside a log call. The user is " +
                "told a cause and an action (FailureCauseText); the exception's own text goes to the log.");
        }

        return found;
    }

    private static bool IsInCommentLine(string text, int offset)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        var leading = text.AsSpan(lineStart, offset - lineStart).TrimStart();
        return leading.StartsWith("//", StringComparison.Ordinal) || leading.StartsWith("*", StringComparison.Ordinal);
    }

    private static XDocument SettingsMarkup()
    {
        var file = Assert.Single(AppSourceFile.LoadAll("SettingsWindow.xaml"));
        return XDocument.Parse(file.Text, LoadOptions.SetLineInfo);
    }
}
