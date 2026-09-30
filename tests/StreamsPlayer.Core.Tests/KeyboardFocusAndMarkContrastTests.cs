using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0162: in both themes every state mark, focus position and menu entry can be seen and reached.
/// </summary>
/// <remarks>
/// The App is not referenced, so the palette is read from <c>ThemeService.cs</c> and the templates from
/// <c>App.xaml</c> as text, like the other source gates. What a test cannot see - that a ring is drawn where
/// the eye is - is the GUI observation the ticket asks for; what it can hold is the arithmetic a palette must
/// satisfy and the shape a template must keep, which are the two that were silently lost.
/// </remarks>
public sealed class KeyboardFocusAndMarkContrastTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // The table row reads ["Key"] = (Color.FromRgb(r, g, b), Color.FromRgb(r, g, b)) or (Colors.White, ...).
    private static readonly Regex PaletteRow = new(
        @"\[""(?<key>\w+)""\]\s*=\s*\((?<light>[^()]*(?:\([^()]*\))?),\s*(?<dark>[^()]*(?:\([^()]*\))?)\)\s*[,\r\n]",
        RegexOptions.Compiled);

    private static readonly Regex Rgb = new(@"FromRgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.Compiled);

    /// <summary>The marks a state is drawn with, each against every surface it can sit on, in the dark theme.</summary>
    [Theory]
    [InlineData("AccentMarkBrush", "ControlBrush")]
    [InlineData("AccentMarkBrush", "ControlHoverBrush")]
    [InlineData("AccentMarkBrush", "SurfaceBrush")]
    [InlineData("AccentMarkBrush", "CardBrush")]
    [InlineData("AccentMarkBrush", "CardSelectedBrush")]
    [InlineData("AccentMarkBrush", "WindowBackground")]
    public void TheDarkAccentMarkReadsAgainstEverySurfaceItSitsOnAsWellAsText(string mark, string surface)
    {
        Assert.True(Contrast(mark, surface, dark: true) >= 4.5, Describe(mark, surface, dark: true));
        Assert.True(Contrast(mark, surface, dark: false) >= 4.5, Describe(mark, surface, dark: false));
    }

    [Fact]
    public void TheTextSelectionStandsOffTheTextBoxAndKeepsItsTextReadable()
    {
        foreach (var dark in new[] { false, true })
        {
            Assert.True(Contrast("SelectionBrush", "ControlBrush", dark) >= 3, Describe("SelectionBrush", "ControlBrush", dark));
            Assert.True(Contrast("SelectionBrush", "AccentTextBrush", dark) >= 4.5, Describe("SelectionBrush", "AccentTextBrush", dark));
        }
    }

    [Fact]
    public void EveryFocusableControlTypeNamesAFocusVisualThatIsNotTheBlackDottedDefault()
    {
        var app = App();
        foreach (var type in new[] { "Button", "CheckBox", "ComboBox", "TabItem", "ListBoxItem", "Slider", "ToggleButton", "Hyperlink" })
        {
            var style = Assert.Single(ImplicitStyles(app, type));
            var value = style.Elements().SingleOrDefault(element =>
                element.Name.LocalName == "Setter" && element.Attribute("Property")?.Value == "FocusVisualStyle")
                ?.Attribute("Value")?.Value;

            Assert.True(
                value is not null && value.Contains("FocusVisual", StringComparison.Ordinal),
                $"The implicit {type} style sets no themed FocusVisualStyle (found: {value ?? "nothing"}).");
        }
    }

    [Fact]
    public void NoMarkupSwitchesTheFocusVisualOffWithoutReplacingIt()
    {
        var offenders = AppSourceFile.LoadAll("*.xaml")
            .Where(file => Regex.IsMatch(file.Text, @"FocusVisualStyle(?:""\s+Value)?\s*=\s*""\{x:Null\}"""))
            .Select(file => file.Name)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheFocusRingsDrawFromTheAccentMarkOrDeclareThemselvesOutOfTheme()
    {
        var app = App();
        foreach (var key in new[] { "ThemedFocusVisual", "ThemedFocusVisualInset" })
        {
            var strokes = KeyedStyle(app, key).Descendants().Where(element => element.Name.LocalName == "Rectangle")
                .Select(element => element.Attribute("Stroke")?.Value).OfType<string>().ToArray();
            Assert.Equal(["{DynamicResource AccentMarkBrush}"], strokes);
        }

        Assert.Contains(
            "Out of theme (APP-STYLE 5",
            KeyedStyle(app, "PlayerOverlayFocusVisual").NodesBeforeSelf().OfType<XComment>().Last().Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheMenuTemplatesKeepAScrollingHostAroundTheirItems()
    {
        var app = App();
        var context = ImplicitStyles(app, "ContextMenu").Single();
        var item = ImplicitStyles(app, "MenuItem").Single();

        Assert.Equal(1, ItemsHostsUnderAScrollViewer(context));
        Assert.Equal(1, ItemsHostsUnderAScrollViewer(item));
    }

    [Fact]
    public void TheStateMarksThatVanishedInTheDarkThemeNowUseTheMarkRoles()
    {
        var app = App();
        var menu = ImplicitStyles(app, "MenuItem").Single();
        var check = menu.Descendants().Single(element => element.Attribute(Xaml + "Name")?.Value == "CheckMark");
        Assert.Equal("{DynamicResource AccentMarkBrush}", check.Attribute("Stroke")?.Value);

        var tab = ImplicitStyles(app, "TabItem").Single();
        var stripe = tab.Descendants().Single(element =>
            element.Name.LocalName == "Setter" &&
            element.Attribute("TargetName")?.Value == "TabBorder" &&
            element.Attribute("Property")?.Value == "BorderBrush" &&
            element.Parent?.Attribute("Property")?.Value == "IsSelected");
        Assert.Equal("{DynamicResource AccentMarkBrush}", stripe.Attribute("Value")?.Value);

        var selection = ImplicitStyles(app, "TextBox").Single().Elements().Single(element =>
            element.Attribute("Property")?.Value == "SelectionBrush");
        Assert.Equal("{DynamicResource SelectionBrush}", selection.Attribute("Value")?.Value);
    }

    [Fact]
    public void TheCompactPanelNoteTurnsOnlyWhileItsIndicatorIsShown()
    {
        var markup = Assert.Single(AppSourceFile.LoadAll("CompactPanelWindow.xaml")).Text;

        // A storyboard begun by the Loaded event is never stopped again; the animation has to hang off the
        // indicator's visibility and end with it.
        Assert.DoesNotContain("RoutedEvent=\"Loaded\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsVisible, ElementName=PlayingIndicator", markup, StringComparison.Ordinal);
        Assert.Contains("<StopStoryboard", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AFrozenBackdropOnlyRecordsTheWashItsPictureWasActuallyDrawnAgainst()
    {
        var session = Assert.Single(AppSourceFile.LoadAll("WaveParticlesBackdropSession.cs"));
        var body = session.Masked[session.Masked.IndexOf("public void EnsureBuffer", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("public void Step", StringComparison.Ordinal)];

        // A carried picture was drawn against the previous surface's colour: recording the new one would let
        // EnsureStill skip the redraw (WAVE-PARTICLES rule 7).
        Assert.Matches(@"if\s*\(\s*carried\s+is\s+null\s*\)\s*\{\s*_lastWash\s*=\s*wash;\s*\}", body);
        Assert.Single(Regex.Matches(body, @"_lastWash\s*="));
    }

    private static int ItemsHostsUnderAScrollViewer(XElement style) =>
        style.Descendants()
            .Count(element => element.Attribute("IsItemsHost")?.Value == "True" &&
                              element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "ScrollViewer"));

    private static XElement App() => XDocument.Parse(Assert.Single(AppSourceFile.LoadAll("App.xaml")).Text).Root!;

    private static IEnumerable<XElement> ImplicitStyles(XElement app, string targetType) =>
        app.Descendants().Where(element =>
            element.Name.LocalName == "Style" &&
            element.Attribute("TargetType")?.Value == targetType &&
            element.Attribute(Xaml + "Key") is null);

    private static XElement KeyedStyle(XElement app, string key) =>
        app.Descendants().Single(element =>
            element.Name.LocalName == "Style" && element.Attribute(Xaml + "Key")?.Value == key);

    private static string Describe(string foreground, string background, bool dark) =>
        $"{foreground} on {background} ({(dark ? "dark" : "light")}) measures {Contrast(foreground, background, dark):0.00}:1.";

    private static double Contrast(string foreground, string background, bool dark)
    {
        var a = Luminance(Colour(foreground, dark));
        var b = Luminance(Colour(background, dark));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static (int R, int G, int B) Colour(string key, bool dark)
    {
        var table = Assert.Single(AppSourceFile.LoadAll("ThemeService.cs")).Text;
        var row = PaletteRow.Matches(table).Single(match => match.Groups["key"].Value == key);
        var cell = row.Groups[dark ? "dark" : "light"].Value.Trim();
        if (cell == "Colors.White")
        {
            return (255, 255, 255);
        }

        var rgb = Rgb.Match(cell);
        Assert.True(rgb.Success, $"Palette key {key} has no opaque FromRgb colour: {cell}");
        return (int.Parse(rgb.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(rgb.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(rgb.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    // WCAG 2.x relative luminance of an sRGB colour.
    private static double Luminance((int R, int G, int B) colour)
    {
        static double Channel(int value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));
    }
}
