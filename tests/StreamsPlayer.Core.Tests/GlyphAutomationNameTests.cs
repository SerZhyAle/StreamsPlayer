using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0114: <c>APP-BEHAVIOUR</c> rule 9 - a control whose only label is a glyph carries an accessible name,
/// the name follows the control's current role, and it survives a change of language.
/// </summary>
/// <remarks>
/// <see cref="TabAutomationNameTests"/> gated tabs and nothing else, so a glyph-only button that lost its
/// name failed nothing. This is the widening the contract's section 13 calls rung 2.
/// <para>
/// Which controls are glyph-only is <em>derived</em>, never listed. A button is glyph-only when the style it
/// resolves to draws no caption (its content template has neither a <c>ContentPresenter</c> nor a text bound
/// to the content), or when it is given no caption to draw. A combo box and a slider always need a name of
/// their own: the label a sighted user reads sits beside them in a separate element. A control with visible
/// text needs nothing - WPF names a button after its caption - so it is left alone.
/// </para>
/// <para>
/// A name has to be a resource reference to a key the English dictionary holds (and, by the parity gate,
/// every other one does), or a binding. A literal is a failure because it cannot follow a language change,
/// and so is a key nobody defines, because it resolves to nothing at run time.
/// </para>
/// <para>
/// The second half reads the code: a button that swaps its glyph style at run time has changed its role
/// (record becomes stop recording, pin becomes unpin), and the block that swaps the style must re-point the
/// name - or, for a captioned button, the caption - by resource reference in the same place. An assigned
/// string would follow the role and then freeze at the language it was assigned in.
/// </para>
/// </remarks>
public sealed class GlyphAutomationNameTests
{
    private const string AutomationName = "AutomationProperties.Name";
    private const string AutomationLabeledBy = "AutomationProperties.LabeledBy";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly HashSet<string> ButtonElements = new(StringComparer.Ordinal)
    {
        "Button", "ToggleButton", "RepeatButton"
    };

    // A combo box or a slider is labelled by a neighbouring text block, never by itself.
    private static readonly HashSet<string> AlwaysNamedElements = new(StringComparer.Ordinal)
    {
        "ComboBox", "Slider"
    };

    private static readonly HashSet<string> CaptionElements = new(StringComparer.Ordinal)
    {
        "TextBlock", "AccessText", "Label", "Run"
    };

    private static readonly Regex ResourceReference = new(
        @"^\{(?:StaticResource|DynamicResource)\s+(?<key>[^{}\s]+)\s*\}$", RegexOptions.Compiled);

    private static readonly Regex DynamicName = new(@"^\{DynamicResource\s+(?<key>[\w.]+)\s*\}$", RegexOptions.Compiled);

    [Fact]
    public void EveryGlyphOnlyControlStatesALocalizedAutomationName()
    {
        var problems = InspectMarkup(ShippedMarkup(), EnglishKeys(), out _);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheMarkupGateActuallyFoundTheGlyphOnlyControls()
    {
        // A derivation that silently matched nothing would pass loudest exactly when it had stopped
        // working. The floor is well under what ships on 2026-09-24 (the compact panel alone has ten).
        InspectMarkup(ShippedMarkup(), EnglishKeys(), out var controls);

        Assert.True(controls >= 40, $"Only {controls} glyph-only controls were found in the application markup.");
    }

    [Fact]
    public void AButtonThatChangesRoleRePointsItsNameByResource()
    {
        var problems = InspectRoleChanges(AppSourceFile.LoadAll("*.cs"), out var swaps);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        // Seven style swaps ship today: the three record buttons, the two transport buttons and the
        // compact panel's pin. A scan that found none would pass by having stopped reading.
        Assert.True(swaps >= 6, $"Only {swaps} run-time style swaps were found in the application sources.");
    }

    [Fact]
    public void TheMarkupGateFailsOnEachWayANameCanBeWrong()
    {
        const string styles = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style x:Key="GlyphButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
                <Setter Property="ContentTemplate"><Setter.Value><DataTemplate>
                  <StackPanel><Path /><ContentPresenter Content="{Binding}" /></StackPanel>
                </DataTemplate></Setter.Value></Setter>
              </Style>
              <Style x:Key="GlyphOnlyButton" TargetType="Button" BasedOn="{StaticResource GlyphButton}">
                <Setter Property="ContentTemplate"><Setter.Value><DataTemplate>
                  <Viewbox><Path /></Viewbox>
                </DataTemplate></Setter.Value></Setter>
              </Style>
              <Style x:Key="StopGlyphOnlyButton" TargetType="Button" BasedOn="{StaticResource GlyphOnlyButton}" />
            </ResourceDictionary>
            """;
        const string window = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel>
                <Button Style="{StaticResource StopGlyphOnlyButton}" />
                <Button Style="{StaticResource StopGlyphOnlyButton}" AutomationProperties.Name="Stop" />
                <Button Style="{StaticResource StopGlyphOnlyButton}" AutomationProperties.Name="{DynamicResource Gone}" />
                <Button Style="{StaticResource StopGlyphOnlyButton}" Content="{DynamicResource Stop}" />
                <Button Style="{StaticResource GlyphButton}" />
                <Button Style="{StaticResource GlyphButton}" Content="{DynamicResource Stop}" />
                <Button><Viewbox><Path /></Viewbox></Button>
                <Button><TextBlock Text="{DynamicResource Stop}" /></Button>
                <ComboBox />
                <Slider AutomationProperties.Name="{DynamicResource Stop}" />
                <Button Style="{StaticResource NoSuchStyle}" />
                <Button Style="{StaticResource StopGlyphOnlyButton}" AutomationProperties.Name="{DynamicResource Stop}" />
              </StackPanel>
            </Window>
            """;

        var problems = InspectMarkup(
            [("App.xaml", styles), ("Sample.xaml", window)], new HashSet<string>(StringComparer.Ordinal) { "Stop" },
            out var controls);

        // Glyph-only: the first four, the unnamed GlyphButton, the path-only button, the combo box, the
        // slider and the last button. The captioned GlyphButton and the TextBlock button are not.
        Assert.Equal(9, controls);
        Assert.Equal(
            ["Sample.xaml:4", "Sample.xaml:5", "Sample.xaml:6", "Sample.xaml:7", "Sample.xaml:8",
             "Sample.xaml:10", "Sample.xaml:12", "Sample.xaml:14"],
            problems.Select(problem => problem[..problem.IndexOf(": ", StringComparison.Ordinal)]).ToArray());
        Assert.Contains("literal", problems[1], StringComparison.Ordinal);
        Assert.Contains("Gone", problems[2], StringComparison.Ordinal);
        Assert.Contains("NoSuchStyle", problems[7], StringComparison.Ordinal);
    }

    [Fact]
    public void TheRoleGateFailsOnAStyleSwapThatKeepsItsNameOrAssignsAString()
    {
        const string source = """
            class Sample
            {
                void Kept(bool on)
                {
                    RecordButton.Style = (Style)FindResource(on ? "StopRecordGlyphOnlyButton" : "RecordGlyphOnlyButton");
                    RecordButton.SetResourceReference(ToolTipProperty, on ? "StopRecordTip" : "RecordTip");
                }

                void Assigned(bool on)
                {
                    RecordButton.Style = (Style)FindResource(on ? "StopRecordGlyphOnlyButton" : "RecordGlyphOnlyButton");
                    AutomationProperties.SetName(RecordButton, LocalizationService.Get(on ? "StopRecord" : "Record"));
                }

                void Named(bool on)
                {
                    RecordButton.Style = (Style)FindResource(on ? "StopRecordGlyphOnlyButton" : "RecordGlyphOnlyButton");
                    RecordButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, on ? "StopRecord" : "Record");
                }

                void Captioned(bool on)
                {
                    // PlayButton.Style = (Style)FindResource("x"); in a comment is not a swap
                    PlayButton.Style = (Style)FindResource(on ? "StopGlyphButton" : "PlayGlyphButton");
                    PlayButton.SetResourceReference(ContentControl.ContentProperty, on ? "Stop" : "Play");
                }
            }
            """;

        var problems = InspectRoleChanges([AppSourceFile.Parse("Sample.cs", source)], out var swaps);

        Assert.Equal(4, swaps);
        Assert.Equal(2, problems.Count);
        Assert.StartsWith("Sample.cs:5:", problems[0], StringComparison.Ordinal);
        Assert.StartsWith("Sample.cs:11:", problems[1], StringComparison.Ordinal);
    }

    /// <summary>Every glyph-only control in the markup that cannot announce itself in every language.</summary>
    private static List<string> InspectMarkup(
        IReadOnlyList<(string Name, string Text)> files, IReadOnlySet<string> keys, out int controls)
    {
        var documents = files
            .Select(file => (file.Name, Document: XDocument.Parse(file.Text, LoadOptions.SetLineInfo)))
            .ToArray();
        var styles = ButtonStyles(documents.Select(document => document.Document));
        var problems = new List<string>();
        var found = 0;

        foreach (var (name, document) in documents)
        {
            foreach (var element in document.Descendants())
            {
                var kind = element.Name.LocalName;
                var where = $"{name}:{((IXmlLineInfo)element).LineNumber}";
                bool glyphOnly;

                // A part inside a control template - the drop-down toggle of the themed combo box - belongs
                // to the control it draws, and that control carries the name. A data template is content,
                // and its buttons are checked like any other.
                if (element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "ControlTemplate"))
                {
                    continue;
                }

                if (AlwaysNamedElements.Contains(kind))
                {
                    glyphOnly = true;
                }
                else if (ButtonElements.Contains(kind))
                {
                    var drawsCaption = DrawsCaption(element, styles, out var unknownStyle);
                    if (unknownStyle is not null)
                    {
                        problems.Add(
                            $"{where}: this button uses the style {unknownStyle}, which no markup file declares, " +
                            "so the gate cannot tell whether it draws a caption.");
                        continue;
                    }

                    glyphOnly = !drawsCaption || !HasCaption(element);
                }
                else
                {
                    continue;
                }

                if (!glyphOnly)
                {
                    continue;
                }

                found++;
                var problem = NameProblem(element, keys);
                if (problem is not null)
                {
                    problems.Add($"{where}: this {kind} {problem}");
                }
            }
        }

        controls = found;
        return problems;
    }

    private static string? NameProblem(XElement element, IReadOnlySet<string> keys)
    {
        if (!string.IsNullOrWhiteSpace(element.Attribute(AutomationLabeledBy)?.Value))
        {
            return null;
        }

        var name = element.Attribute(AutomationName)?.Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            return $"shows no text and sets no {AutomationName}, so it announces nothing to a screen reader " +
                   "and cannot be found by name from outside the process.";
        }

        if (name.StartsWith("{Binding", StringComparison.Ordinal))
        {
            return null;
        }

        var reference = DynamicName.Match(name);
        if (!reference.Success)
        {
            return $"names itself with the literal \"{name}\", which cannot follow a language change. Use " +
                   "{DynamicResource Key}.";
        }

        var key = reference.Groups["key"].Value;
        return keys.Contains(key)
            ? null
            : $"names itself with the resource {key}, which the English dictionary does not define.";
    }

    /// <summary>Whether the button's resolved style draws the content it is given.</summary>
    private static bool DrawsCaption(
        XElement button, IReadOnlyDictionary<string, ButtonStyle> styles, out string? unknownStyle)
    {
        unknownStyle = null;
        var key = StyleKey(button.Attribute("Style")?.Value);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (key is not null)
        {
            if (!visited.Add(key))
            {
                return true;
            }

            if (!styles.TryGetValue(key, out var style))
            {
                unknownStyle = key;
                return true;
            }

            if (style.DrawsCaption is { } draws)
            {
                return draws;
            }

            key = style.BasedOn;
        }

        // No style, or a chain that ends at the framework's own button: that draws its content.
        return true;
    }

    /// <summary>Whether the markup hands the button a caption: a content value or text among its children.</summary>
    private static bool HasCaption(XElement button)
    {
        if (!string.IsNullOrWhiteSpace(button.Attribute("Content")?.Value))
        {
            return true;
        }

        var content = button.Elements().Where(child => !child.Name.LocalName.Contains('.', StringComparison.Ordinal));
        return button.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)) ||
               content.Any(child => CaptionElements.Contains(child.Name.LocalName) ||
                                    child.Descendants().Any(inner => CaptionElements.Contains(inner.Name.LocalName)));
    }

    private sealed record ButtonStyle(string? BasedOn, bool? DrawsCaption);

    /// <summary>
    /// Every keyed button style in the markup, with its base and - where it sets one - whether its content
    /// template draws the content.
    /// </summary>
    private static Dictionary<string, ButtonStyle> ButtonStyles(IEnumerable<XDocument> documents)
    {
        var all = documents.SelectMany(document => document.Descendants()).ToArray();
        var templates = all
            .Where(element => element.Name.LocalName == "DataTemplate" && element.Attribute(Xaml + "Key") is not null)
            .GroupBy(element => element.Attribute(Xaml + "Key")!.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var styles = new Dictionary<string, ButtonStyle>(StringComparer.Ordinal);

        foreach (var style in all.Where(element => element.Name.LocalName == "Style"))
        {
            var key = style.Attribute(Xaml + "Key")?.Value;
            var target = style.Attribute("TargetType")?.Value;
            if (key is null || target is null || !ButtonElements.Contains(target.Replace("{x:Type ", "").TrimEnd('}').Trim()))
            {
                continue;
            }

            var setter = style.Elements()
                .Where(element => element.Name.LocalName == "Setter")
                .FirstOrDefault(element => element.Attribute("Property")?.Value == "ContentTemplate");
            bool? draws = null;
            if (setter is not null)
            {
                var inline = setter.Descendants().FirstOrDefault(element => element.Name.LocalName == "DataTemplate");
                var referenced = StyleKey(setter.Attribute("Value")?.Value);
                var template = inline ?? (referenced is not null && templates.TryGetValue(referenced, out var shared) ? shared : null);
                draws = template is null || TemplateDrawsContent(template);
            }

            styles.TryAdd(key, new ButtonStyle(StyleKey(style.Attribute("BasedOn")?.Value), draws));
        }

        return styles;
    }

    private static bool TemplateDrawsContent(XElement template) =>
        template.Descendants().Any(element =>
            element.Name.LocalName == "ContentPresenter" ||
            (CaptionElements.Contains(element.Name.LocalName) && element.Attribute("Text")?.Value == "{Binding}"));

    /// <summary>The key a style reference names, or null for none and for the framework's own type style.</summary>
    private static string? StyleKey(string? reference)
    {
        if (reference is null)
        {
            return null;
        }

        var match = ResourceReference.Match(reference.Trim());
        return match.Success ? match.Groups["key"].Value : null;
    }

    private static readonly Regex StyleSwap = new(
        @"(?<control>\b\w+)\s*\.\s*Style\s*=\s*\(\s*Style\s*\)\s*(?:Try)?FindResource\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Every run-time style swap whose block does not also re-point the control's name or caption by
    /// resource reference.
    /// </summary>
    private static List<string> InspectRoleChanges(IReadOnlyList<AppSourceFile> files, out int swaps)
    {
        var problems = new List<string>();
        var found = 0;

        foreach (var file in files)
        {
            foreach (Match match in StyleSwap.Matches(file.Masked))
            {
                found++;
                var control = match.Groups["control"].Value;
                var block = EnclosingBlock(file.Masked, match.Index);
                var rePoint = new Regex(
                    $@"\b{Regex.Escape(control)}\s*\.\s*SetResourceReference\s*\(\s*[\w.]*\b(?:NameProperty|ContentProperty)\s*,");
                if (!rePoint.IsMatch(block))
                {
                    problems.Add(
                        $"{file.Name}:{file.LineAt(match.Index)}: {control} swaps its style - its role - here, and " +
                        "this block does not re-point its accessible name (or caption) with SetResourceReference. " +
                        "A name left behind announces the old role; a name assigned as a string freezes at the " +
                        "language it was assigned in.");
                }
            }
        }

        swaps = found;
        return problems;
    }

    /// <summary>The innermost brace block around an offset, on the masked text where no brace is in a string.</summary>
    private static string EnclosingBlock(string masked, int offset)
    {
        var depth = 0;
        var start = 0;
        for (var position = offset; position >= 0; position--)
        {
            if (masked[position] == '}')
            {
                depth++;
            }
            else if (masked[position] == '{' && depth-- == 0)
            {
                start = position;
                break;
            }
        }

        depth = 0;
        for (var position = start; position < masked.Length; position++)
        {
            if (masked[position] == '{')
            {
                depth++;
            }
            else if (masked[position] == '}' && --depth == 0)
            {
                return masked[start..(position + 1)];
            }
        }

        return masked[start..];
    }

    private static IReadOnlyList<(string Name, string Text)> ShippedMarkup() =>
        [.. AppSourceFile.LoadAll("*.xaml").Select(file => (file.Name, file.Text))];

    private static IReadOnlySet<string> EnglishKeys() =>
        new HashSet<string>(
            LocalizationDictionary.Load(Path.Combine(LocalizationDictionary.Directory, "Localization.en.xaml")).KeysInFileOrder,
            StringComparer.Ordinal);
}
