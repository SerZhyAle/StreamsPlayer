using System.Xml;
using System.Xml.Linq;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0064: every tab in the shipped markup carries an accessible name.
/// </summary>
/// <remarks>
/// A <c>TabItem</c> whose header is a panel of a glyph and a text block reports no name at all - WPF derives
/// a name from header *text*, and a composite header has none. The defect is invisible from inside the
/// process: the tab is drawn and labelled correctly, and only a screen reader or an out-of-process UI
/// Automation search reveals that the control has nothing to announce. That is also what makes it worth a
/// gate rather than a one-time fix - the Settings tab strip shipped this way for the window's whole life,
/// and nothing would have reported a seventh tab repeating it.
/// <para>
/// The markup is read the same way SP-0057 reads it: the application's own <c>*.xaml</c> is linked into this
/// project as test <em>data</em>, so the Tests -&gt; Core dependency direction is untouched. XAML is XML, so
/// this gate parses rather than masks - an element named <c>TabItem</c> is a tab, while the
/// <c>TargetType="TabItem"</c> of the theme's style in <c>App.xaml</c> is an attribute value and is
/// correctly not one.
/// </para>
/// </remarks>
public sealed class TabAutomationNameTests
{
    private const string AutomationName = "AutomationProperties.Name";

    [Fact]
    public void EveryTabStatesItsOwnAutomationName()
    {
        var problems = Inspect(out _);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheGateActuallyFoundTheTabs()
    {
        // A parse that silently matched nothing would pass this gate loudest exactly when it had stopped
        // working. The floor is the six tabs the Settings window ships today.
        Inspect(out var tabs);

        Assert.True(tabs >= 6, $"Only {tabs} TabItem elements were found in the application markup.");
    }

    /// <summary>Reads the linked application markup and reports every tab that cannot announce itself.</summary>
    private static IReadOnlyList<string> Inspect(out int tabs)
    {
        var problems = new List<string>();
        var found = 0;

        foreach (var file in AppSourceFile.LoadAll("*.xaml"))
        {
            var document = XDocument.Parse(file.Text, LoadOptions.SetLineInfo);
            foreach (var tab in document.Descendants().Where(element => element.Name.LocalName == "TabItem"))
            {
                found++;
                var where = $"{file.Name}:{((IXmlLineInfo)tab).LineNumber}";
                var name = tab.Attribute(AutomationName)?.Value;

                if (string.IsNullOrWhiteSpace(name))
                {
                    problems.Add(
                        $"{where}: this tab sets no {AutomationName}. Its header is a panel, so the tab " +
                        "reports no name to a screen reader and cannot be selected by name from outside " +
                        "the process.");
                    continue;
                }

                // The header's own label is the only correct value: a name that says something else is a
                // second string to keep translated, and one that names the wrong tab is worse than none.
                var label = HeaderLabel(tab);
                if (label is not null && name != label)
                {
                    problems.Add(
                        $"{where}: this tab announces {name} while its header shows {label}. The " +
                        "accessible name must be the label the user reads.");
                }
            }
        }

        tabs = found;
        return problems;
    }

    /// <summary>
    /// The single resource binding the tab's header displays, or <c>null</c> when it shows something else.
    /// </summary>
    /// <remarks>
    /// Null rather than a failure: a header that draws its label some other way is a legitimate design that
    /// this gate has no opinion about. The name being present at all is the part that is not optional.
    /// </remarks>
    private static string? HeaderLabel(XElement tab)
    {
        var header = tab.Elements().FirstOrDefault(element => element.Name.LocalName == "TabItem.Header");
        if (header is null)
        {
            return null;
        }

        var texts = header.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => element.Attribute("Text")?.Value)
            .Where(text => text is not null && text.StartsWith("{DynamicResource ", StringComparison.Ordinal))
            .ToArray();

        return texts.Length == 1 ? texts[0] : null;
    }
}
