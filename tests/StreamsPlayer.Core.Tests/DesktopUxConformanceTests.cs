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
        "TvScheduleRemove_Click"
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
