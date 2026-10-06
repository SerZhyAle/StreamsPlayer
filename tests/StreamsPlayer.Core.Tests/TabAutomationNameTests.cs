using System.Xml;
using System.Xml.Linq;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0064: every destination of the settings navigation carries an accessible name.
/// </summary>
/// <remarks>
/// A navigation row whose content is a panel of a glyph and a text block reports no name at all - WPF
/// derives a name from *text* content, and a composite row has none to announce. The defect is invisible
/// from inside the process: the row is drawn and labelled correctly, and only a screen reader or an
/// out-of-process UI Automation search reveals that the control has nothing to announce. That is also
/// what makes it worth a gate rather than a one-time fix - the Settings tab strip shipped this way for
/// the window's whole life, and nothing would have reported a seventh entry repeating it.
/// <para>
/// The markup is read the same way SP-0057 reads it: the application's own <c>*.xaml</c> is linked into
/// this project as test <em>data</em>, so the Tests -&gt; Core dependency direction is untouched. XAML is
/// XML, so this gate parses rather than masks.
/// </para>
/// <para>
/// SP-0191 moved the settings navigation from a <c>TabControl</c> to a list of destinations (the
/// WINDOWS-UI profile's vertical page list); the gate follows the surface, not the control class - the
/// declared <c>AutomationProperties.Name</c> is what a screen reader and an outside driver consume.
/// </para>
/// </remarks>
public sealed class TabAutomationNameTests
{
    private const string AutomationName = "AutomationProperties.Name";

    [Fact]
    public void EverySettingsNavigationDestinationStatesItsOwnAutomationName()
    {
        var problems = Inspect(out _);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheGateActuallyFoundTheTabs()
    {
        // A parse that silently matched nothing would pass this gate loudest exactly when it had stopped
        // working. The floor is the destinations the Settings window ships today: General, Library,
        // Playback, Audio, Files, About.
        Inspect(out var destinations);

        Assert.True(destinations >= 4, $"Only {destinations} settings navigation destinations were found in the application markup.");
    }

    /// <summary>Reads the linked application markup and reports every destination that cannot announce itself.</summary>
    private static IReadOnlyList<string> Inspect(out int destinations)
    {
        var problems = new List<string>();
        var found = 0;

        foreach (var file in AppSourceFile.LoadAll("*.xaml"))
        {
            var document = XDocument.Parse(file.Text, LoadOptions.SetLineInfo);
            foreach (var row in document.Descendants().Where(element => element.Name.LocalName == "ListBoxItem"
                && element.Attribute(AutomationName)?.Value.StartsWith("{DynamicResource Settings", StringComparison.Ordinal) == true))
            {
                found++;
                var where = $"{file.Name}:{((IXmlLineInfo)row).LineNumber}";
                var name = row.Attribute(AutomationName)?.Value;

                if (string.IsNullOrWhiteSpace(name))
                {
                    problems.Add(
                        $"{where}: this navigation row sets no {AutomationName}. Its content is a panel, so the " +
                        "row reports no name to a screen reader and cannot be selected by name from outside " +
                        "the process.");
                    continue;
                }

                // The row's own label is the only correct value: a name that says something else is a
                // second string to keep translated, and one that names the wrong destination is worse
                // than none.
                var label = RowLabel(row);
                if (label is not null && name != label)
                {
                    problems.Add(
                        $"{where}: this row announces {name} while its label shows {label}. The " +
                        "accessible name must be the label the user reads.");
                }
            }
        }

        destinations = found;
        return problems;
    }

    /// <summary>
    /// The single resource binding the row's caption displays, or <c>null</c> when it shows something else.
    /// </summary>
    /// <remarks>
    /// Null rather than a failure: a row that draws its label some other way is a legitimate design that
    /// this gate has no opinion about. The name being present at all is the part that is not optional.
    /// </remarks>
    private static string? RowLabel(XElement row)
    {
        var texts = row.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => element.Attribute("Text")?.Value)
            .Where(text => text is not null && text.StartsWith("{DynamicResource ", StringComparison.Ordinal))
            .ToArray();

        return texts.Length == 1 ? texts[0] : null;
    }
}
