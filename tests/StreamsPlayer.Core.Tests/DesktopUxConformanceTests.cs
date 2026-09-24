using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0109: two <c>APP-BEHAVIOUR</c> rules this product owns and had broken, turned from a reading of the
/// code into a gate so neither can quietly come back.
/// </summary>
/// <remarks>
/// Rule 12 - a settings window commits on its own button: nothing in the window that offers Save and
/// Cancel may do anything before Save. Rule 6 - a failure is a set of actions: the exception's own text
/// goes to the log, never into what the user reads. The application's sources are read as test data, the
/// way <see cref="TabAutomationNameTests"/> and the call-site gate read them.
/// </remarks>
public sealed class DesktopUxConformanceTests
{
    /// <summary>
    /// Every event handler the Settings markup may name, and why each one is allowed beside Cancel. A new
    /// handler fails this gate until it is added here with its reason - which is the moment to ask whether
    /// it edits a pending value or does something, and to put it in the Tools window if it does something.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SettingsHandlers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["FrameFolderBrowse_Click"] = "edits the pending frame folder",
        ["FrameFolderReset_Click"] = "edits the pending frame folder",
        ["FrameFolderOpen_Click"] = "opens the folder in Explorer and changes nothing",
        ["OpenLink_Click"] = "opens a web page and changes nothing",
        ["Save_Click"] = "the commit",
        ["Cancel_Click"] = "the no-action exit"
    };

    private static readonly HashSet<string> EventAttributes = new(StringComparer.Ordinal)
    {
        "Click", "Checked", "Unchecked", "SelectionChanged", "TextChanged", "ValueChanged",
        "MouseDoubleClick", "MouseLeftButtonUp", "PreviewMouseLeftButtonUp", "KeyDown", "PreviewKeyDown",
        "LostFocus", "DropDownClosed"
    };

    [Fact]
    public void TheSettingsWindowNamesOnlyHandlersThatChangeNothingBeforeSave()
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
                        "is not on the list of handlers that change nothing before Save. An operation that commits " +
                        "on its own belongs in the Tools window, where no Cancel promises to undo it.");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(handlers >= SettingsHandlers.Count, $"Only {handlers} handlers were found in the Settings markup.");
    }

    [Fact]
    public void TheSettingsCancelButtonIsAlsoEscape()
    {
        var cancel = Assert.Single(SettingsMarkup().Descendants(), element =>
            element.Attribute("Click")?.Value == "Cancel_Click");

        // Rule 1: Escape, the close box and Cancel are one path. Without IsCancel the button works and
        // Escape silently does nothing, which is exactly how this window shipped until SP-0109.
        Assert.Equal("True", cancel.Attribute("IsCancel")?.Value);
    }

    [Fact]
    public void TheSettingsWindowIsGivenNoWayToRunAnOperation()
    {
        var code = Assert.Single(AppSourceFile.LoadAll("SettingsWindow.xaml.cs"));

        Assert.DoesNotContain("ToolsAction", code.Masked, StringComparison.Ordinal);
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
