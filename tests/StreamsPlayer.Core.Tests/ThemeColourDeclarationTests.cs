using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0114: <c>APP-STYLE</c> sections 3 and 5 - every themed colour lives in one palette table and is
/// referenced dynamically, and a colour held out of the theme is declared where it is defined.
/// </summary>
/// <remarks>
/// The contract's own reading of this product found literal colours that nothing declared: white text on
/// the accent button beside an accent-ink key made for it, status dots picked from the framework's named
/// brushes, a recording red on an ordinary themed button, and the grid tiles, the player and the compact
/// panel with the reason written only for some of them. A literal colour is not the defect - a picture
/// needs its own backing and a video needs black - an <em>undeclared</em> one is, because the next reader
/// cannot tell a decision from an oversight, and a theme switch shows the difference only to someone who
/// happens to look at that one control.
/// <para>
/// So a literal colour passes in exactly two places: the palette table itself (the design-time defaults in
/// <c>App.xaml</c>, one per <c>ThemeService</c> key), or under a declaration. In markup the declaration is a
/// comment carrying <see cref="Marker"/> immediately before the element, before any ancestor, as the
/// element's first child (a window declaring itself), or before any style the element's style is based on
/// - so the player overlay's styles inherit the one declaration their base carries. In code it is a
/// comment carrying the marker anywhere in the file that holds the literal; <c>ThemeService.cs</c> is the
/// table and needs none.
/// </para>
/// <para>
/// Two cheaper rungs of the contract's section 7 ladder ride along, because they read the same inputs: a
/// palette key referenced statically (rung 1, the defect that silently removes an element from the theme)
/// and a palette whose markup defaults and code table disagree about which roles exist (rung 2).
/// </para>
/// </remarks>
public sealed class ThemeColourDeclarationTests
{
    /// <summary>The words that declare a surface out of theme. Kept short so a reason can follow it.</summary>
    internal const string Marker = "Out of theme (APP-STYLE 5";

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly HashSet<string> ColourProperties = new(StringComparer.Ordinal)
    {
        "Foreground", "Background", "Fill", "Stroke", "BorderBrush", "Color", "CaretBrush", "SelectionBrush",
        "OpacityMask"
    };

    // Transparent is the absence of a colour, and {x:Null} is the absence of a brush.
    private static readonly HashSet<string> NotAColour = new(StringComparer.Ordinal) { "Transparent" };

    private static readonly Regex StaticReference = new(@"\{StaticResource\s+(?<key>[\w.]+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex StyleReference = new(
        @"^\{(?:StaticResource|DynamicResource)\s+(?<key>[^{}\s]+)\s*\}$", RegexOptions.Compiled);
    private static readonly Regex PaletteEntry = new(@"\[\s*""(?<key>\w+)""\s*\]\s*=\s*\(", RegexOptions.Compiled);

    [Fact]
    public void EveryLiteralColourInTheMarkupIsThePaletteOrDeclared()
    {
        var problems = InspectMarkup(ShippedMarkup(), PaletteKeys(), out _);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheMarkupSweepActuallyFoundTheColours()
    {
        // A sweep that matched nothing would pass loudest exactly when it had stopped reading. The floor is
        // well under what ships on 2026-09-24: the palette defaults alone are twenty-two.
        InspectMarkup(ShippedMarkup(), PaletteKeys(), out var colours);

        Assert.True(colours >= 60, $"Only {colours} literal colours were found in the application markup.");
    }

    [Fact]
    public void EveryLiteralColourInTheCodeIsThePaletteOrDeclared()
    {
        var problems = InspectCode(AppSourceFile.LoadAll("*.cs"), PaletteKeys(), out var colours);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        // The palette table and the two declared tables (monogram plates, signal-health stripe).
        Assert.True(colours >= 40, $"Only {colours} literal colours were found in the application sources.");
    }

    [Fact]
    public void ThePaletteTableAndItsMarkupDefaultsNameTheSameRoles()
    {
        var code = PaletteKeys();
        var markup = MarkupPaletteKeys(Assert.Single(ShippedMarkup(), file => file.Name == "App.xaml").Text);

        Assert.True(code.Count >= 22, $"Only {code.Count} palette entries were read from ThemeService.cs.");
        Assert.Empty(code.Except(markup));
        Assert.Empty(markup.Except(code));
    }

    /// <summary>
    /// SP-0211, <c>APP-SETTINGS</c> rule 6: while Windows is in high contrast every role follows the system
    /// colours. A role added to the table without a high-contrast mapping would silently keep its authored
    /// hue there, which is the failure the rule exists to prevent.
    /// </summary>
    [Fact]
    public void EveryPaletteRoleHasAHighContrastMappingExceptTheContentPlate()
    {
        // The plate under a station's own artwork is content, not chrome (ThemeService.HighContrast.cs).
        var contentPlates = new HashSet<string>(StringComparer.Ordinal) { "FaviconPlateBrush" };
        var mapping = Assert.Single(AppSourceFile.LoadAll("ThemeService.HighContrast.cs")).Text;
        var mapped = Regex.Matches(mapping, @"\[\s*""(?<key>\w+)""\s*\]\s*=\s*(?!\()")
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(PaletteKeys().Except(contentPlates).Except(mapped));
        Assert.Empty(mapped.Except(PaletteKeys()));
        Assert.DoesNotContain(contentPlates, mapped.Contains);
    }

    [Fact]
    public void TheMarkupSweepFailsOnEachWayAColourCanEscapeTheTheme()
    {
        const string app = """
            <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Application.Resources>
                <SolidColorBrush x:Key="TextBrush" Color="#FF172236" />
                <SolidColorBrush x:Key="Stray" Color="#FF123456" />
                <!-- Out of theme (APP-STYLE 5): drawn over video. -->
                <Style x:Key="OverlayButton" TargetType="Button">
                  <Setter Property="Foreground" Value="White" />
                </Style>
                <Style x:Key="OverlayStopButton" TargetType="Button" BasedOn="{StaticResource OverlayButton}">
                  <Setter Property="Foreground" Value="#FFFF4D4D" />
                </Style>
                <Style x:Key="ThemedButton" TargetType="Button">
                  <Setter Property="Foreground" Value="#FFFF4D4D" />
                  <Setter Property="Background" Value="Transparent" />
                  <Setter Property="BorderBrush" Value="{DynamicResource TextBrush}" />
                </Style>
              </Application.Resources>
            </Application>
            """;
        const string window = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="Black">
              <!-- Out of theme (APP-STYLE 5, a content-coloured window): video. -->
              <Grid>
                <TextBlock Foreground="White" />
              </Grid>
            </Window>
            """;
        const string dialog = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel>
                <Button Background="{DynamicResource AccentBrush}" Foreground="White" />
                <Button Style="{StaticResource OverlayStopButton}" Background="#FF000000" />
                <TextBlock Foreground="{StaticResource TextBrush}" />
                <Border>
                  <Border.Background>
                    <LinearGradientBrush><GradientStop Color="#00111827" /></LinearGradientBrush>
                  </Border.Background>
                </Border>
              </StackPanel>
            </Window>
            """;

        var problems = InspectMarkup(
            [("App.xaml", app), ("Player.xaml", window), ("Dialog.xaml", dialog)],
            new HashSet<string>(StringComparer.Ordinal) { "TextBrush" },
            out var colours);

        // Every literal but Transparent: the palette default, the stray brush, the three setters, the
        // window background and its text, the white caption, the overlay button's background, the stop.
        Assert.Equal(10, colours);
        Assert.Equal(
            ["App.xaml:5", "App.xaml:14", "Dialog.xaml:3", "Dialog.xaml:5", "Dialog.xaml:8"],
            problems.Select(problem => problem[..problem.IndexOf(": ", StringComparison.Ordinal)]).ToArray());
        Assert.Contains("StaticResource", problems[3], StringComparison.Ordinal);
    }

    [Fact]
    public void TheCodeSweepFailsOnAnUndeclaredLiteralAndPassesADeclaredOne()
    {
        const string undeclared = """
            class Row
            {
                // Brushes.Red in a comment is not a use
                Brush Status => ok ? Brushes.ForestGreen : Brushes.Transparent;
                Color Mark => Color.FromRgb(178, 34, 34);
                Color Mixed => Color.FromRgb(level, level, level);
                Brush Plate => Parse("#FF336699");
                object Accent => FindResource("AccentBrush");
            }
            """;
        const string declared = """
            // Out of theme (APP-STYLE 5): a picture's own plate.
            class Plate
            {
                Brush Ink => Brushes.White;
            }
            """;

        var problems = InspectCode(
            [AppSourceFile.Parse("Row.cs", undeclared), AppSourceFile.Parse("Plate.cs", declared)],
            new HashSet<string>(StringComparer.Ordinal) { "AccentBrush" },
            out var colours);

        Assert.Equal(4, colours);
        Assert.Equal(
            ["Row.cs:4", "Row.cs:5", "Row.cs:7", "Row.cs:8"],
            problems.Select(problem => problem[..problem.IndexOf(": ", StringComparison.Ordinal)]).ToArray());
    }

    /// <summary>Every literal colour in the markup that is neither the palette table nor declared.</summary>
    private static List<string> InspectMarkup(
        IReadOnlyList<(string Name, string Text)> files, IReadOnlySet<string> palette, out int colours)
    {
        var documents = files
            .Select(file => (file.Name, Document: XDocument.Parse(file.Text, LoadOptions.SetLineInfo)))
            .ToArray();
        var styles = documents
            .SelectMany(document => document.Document.Descendants())
            .Where(element => element.Name.LocalName == "Style" && element.Attribute(Xaml + "Key") is not null)
            .GroupBy(element => element.Attribute(Xaml + "Key")!.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var problems = new List<string>();
        var found = 0;

        foreach (var (name, document) in documents)
        {
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    var where = $"{name}:{((IXmlLineInfo)attribute).LineNumber}";
                    foreach (Match reference in StaticReference.Matches(attribute.Value))
                    {
                        if (palette.Contains(reference.Groups["key"].Value))
                        {
                            problems.Add(
                                $"{where}: {attribute.Name.LocalName} reads the palette key {reference.Groups["key"].Value} " +
                                "as a StaticResource, which takes this element out of the theme for good. Use " +
                                "DynamicResource.");
                        }
                    }

                    if (!IsLiteralColour(element, attribute))
                    {
                        continue;
                    }

                    found++;
                    if (IsPaletteDefault(name, element, palette) || IsDeclared(element, styles))
                    {
                        continue;
                    }

                    problems.Add(
                        $"{where}: {Describe(element, attribute)} is the literal colour {attribute.Value}, which the " +
                        "theme cannot reach and nothing declares. Take it from the palette by DynamicResource, or " +
                        $"declare the surface where it is defined with a comment beginning \"{Marker}\" and the reason.");
                }
            }
        }

        colours = found;
        return problems;
    }

    private static bool IsLiteralColour(XElement element, XAttribute attribute)
    {
        var value = attribute.Value.Trim();
        if (value.Length == 0 || value.StartsWith('{') || NotAColour.Contains(value))
        {
            return false;
        }

        var property = attribute.Name.LocalName;
        if (element.Name.LocalName == "Setter" && property == "Value")
        {
            property = element.Attribute("Property")?.Value ?? string.Empty;
        }

        return ColourProperties.Contains(property[(property.LastIndexOf('.') + 1)..]);
    }

    private static string Describe(XElement element, XAttribute attribute) =>
        element.Name.LocalName == "Setter"
            ? $"the {element.Attribute("Property")?.Value} setter"
            : $"{element.Name.LocalName}.{attribute.Name.LocalName}";

    /// <summary>A design-time default of a palette role: a keyed brush in App.xaml's own resources.</summary>
    private static bool IsPaletteDefault(string file, XElement element, IReadOnlySet<string> palette) =>
        file == "App.xaml" &&
        element.Name.LocalName == "SolidColorBrush" &&
        element.Attribute(Xaml + "Key")?.Value is { } key &&
        palette.Contains(key);

    /// <summary>
    /// Whether the element, an ancestor, or a style it resolves to or is based on carries the declaration.
    /// </summary>
    private static bool IsDeclared(XElement element, IReadOnlyDictionary<string, XElement> styles)
    {
        foreach (var candidate in element.AncestorsAndSelf())
        {
            if (CarriesDeclaration(candidate) || StyleChainDeclared(candidate, styles))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StyleChainDeclared(XElement element, IReadOnlyDictionary<string, XElement> styles)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var reference = element.Name.LocalName == "Style"
            ? element.Attribute("BasedOn")?.Value
            : element.Attribute("Style")?.Value;

        while (reference is not null && StyleReference.Match(reference.Trim()) is { Success: true } match)
        {
            var key = match.Groups["key"].Value;
            if (!visited.Add(key) || !styles.TryGetValue(key, out var style))
            {
                return false;
            }

            if (CarriesDeclaration(style))
            {
                return true;
            }

            reference = style.Attribute("BasedOn")?.Value;
        }

        return false;
    }

    /// <summary>A marker comment right before the element, or as its first child.</summary>
    private static bool CarriesDeclaration(XElement element)
    {
        var before = element.NodesBeforeSelf().Reverse().FirstOrDefault(node => !IsWhitespace(node));
        var first = element.Nodes().FirstOrDefault(node => !IsWhitespace(node));
        return IsMarker(before) || IsMarker(first);
    }

    private static bool IsWhitespace(XNode node) => node is XText text && string.IsNullOrWhiteSpace(text.Value);

    private static bool IsMarker(XNode? node) =>
        node is XComment comment && comment.Value.Contains(Marker, StringComparison.Ordinal);

    private static readonly Regex[] CodeColours =
    [
        new(@"\b(?:Brushes|Colors)\s*\.\s*(?<name>[A-Z]\w*)", RegexOptions.Compiled),
        new(@"\bColor\s*\.\s*From(?:A?Rgb|ScRgb)\s*\(\s*(?:0x[0-9A-Fa-f]+|\d+)(?:\s*,\s*(?:0x[0-9A-Fa-f]+|\d+))*\s*\)",
            RegexOptions.Compiled)
    ];

    private static readonly Regex HexColour = new(
        @"^#(?:[0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

    private static readonly Regex ResourceLookup = new(
        @"(?:\b(?:Try)?FindResource\s*\(|\bResources\s*\[)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Every literal colour in the code outside the palette table and outside a declaring file, and every
    /// palette key read by a one-time lookup.
    /// </summary>
    private static List<string> InspectCode(IReadOnlyList<AppSourceFile> files, IReadOnlySet<string> palette, out int colours)
    {
        var problems = new List<string>();
        var found = 0;

        foreach (var file in files)
        {
            var isTable = file.Name == "ThemeService.cs";
            var declared = file.Text.Contains(Marker, StringComparison.Ordinal);
            var uses = new List<(int Offset, string Text)>();

            foreach (var pattern in CodeColours)
            {
                foreach (Match match in pattern.Matches(file.Masked))
                {
                    if (!NotAColour.Contains(match.Groups["name"].Value))
                    {
                        uses.Add((match.Index, match.Value));
                    }
                }
            }

            foreach (var (offset, value) in file.Literals)
            {
                if (file.Masked[offset] != '"')
                {
                    continue;
                }

                if (HexColour.IsMatch(value))
                {
                    uses.Add((offset, $"\"{value}\""));
                }
                else if (!isTable && palette.Contains(value) && ResourceLookup.IsMatch(file.Masked[..offset]))
                {
                    problems.Add(
                        $"{file.Name}:{file.LineAt(offset)}: the palette key {value} is read by lookup, which " +
                        "copies today's brush and stops following the theme. Use SetResourceReference.");
                }
            }

            found += uses.Count;
            if (isTable || declared)
            {
                continue;
            }

            problems.AddRange(uses.Select(use =>
                $"{file.Name}:{file.LineAt(use.Offset)}: {use.Text} is a literal colour the theme cannot reach, and " +
                $"this file declares no surface out of theme. Use a palette role, or say why with a comment " +
                $"beginning \"{Marker}\"."));
        }

        colours = found;
        return [.. problems.OrderBy(problem => problem, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<(string Name, string Text)> ShippedMarkup() =>
        [.. AppSourceFile.LoadAll("*.xaml").Select(file => (file.Name, file.Text))];

    /// <summary>The roles ThemeService's table defines - the one place a themed colour may be declared.</summary>
    private static IReadOnlySet<string> PaletteKeys()
    {
        var table = Assert.Single(AppSourceFile.LoadAll("ThemeService.cs"));
        return PaletteEntry.Matches(table.Text).Select(match => match.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> MarkupPaletteKeys(string appMarkup) =>
        XDocument.Parse(appMarkup).Root!
            .Descendants()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .Select(element => element.Attribute(Xaml + "Key")?.Value)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
}
