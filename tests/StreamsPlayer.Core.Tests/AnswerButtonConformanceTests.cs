using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0218: this product as an adopter of <c>WINDOWS-UI</c> 0.4 section 11 and <c>ICON-RENDER</c> 0.19
/// section 14 - every command button carries a glyph, and the answers of a dialog the product draws take
/// the answer look.
/// </summary>
/// <remarks>
/// What is held here is this product's side, read from the sources as data (the store is not part of a
/// clone and the App is never loaded): the answer hues and the tint reach the contract's contrast figures on
/// the faces they are actually drawn on, every button is a glyph button or a declared text link, and the
/// answers of the dialogs the product draws are drawn as answers. Escape, Enter, default focus and order
/// are the existing gates' (<c>DesktopUxConformanceTests</c>) and are not restated.
/// </remarks>
public sealed class AnswerButtonConformanceTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Regex StaticKey = new(@"\{StaticResource\s+(?<key>[\w.\-]+)\s*\}", RegexOptions.Compiled);

    // Windows the product draws that are not secondary dialogs: the two companion surfaces, the settings
    // window (it commits as it goes and has no answers of its own) and the list/grid shell.
    private static readonly HashSet<string> NotDialogs = new(StringComparer.OrdinalIgnoreCase)
    {
        "MainWindow.xaml", "PlayerWindow.xaml", "CompactPanelWindow.xaml", "SettingsWindow.xaml"
    };

    [Fact]
    public void TheAnswerHuesReachTheContractsFiguresOnTheFacesTheyAreDrawnOn()
    {
        // WINDOWS-UI 0.4 section 11 item 6: a glyph holds 3:1 and a caption 4.5:1 against the face it is
        // drawn on, the tinted face included. The pressed face lasts as long as the mouse button is down and
        // is feedback, the same convention IconographyConformanceTests applies to every glyph role.
        var palette = IconographyConformanceTests.Palette();
        var tint = Tint();
        var problems = new List<string>();
        for (var theme = 0; theme < 2; theme++)
        {
            var name = theme == 0 ? "light" : "dark";
            foreach (var hue in new[] { "SuccessBrush", "DangerBrush" })
            {
                foreach (var face in new[] { "ControlBrush", "ControlHoverBrush" })
                {
                    var drawn = Blend(palette[hue][theme], palette[face][theme], tint);
                    var glyph = IconographyConformanceTests.Contrast(palette[hue][theme], drawn);
                    var caption = IconographyConformanceTests.Contrast(palette["TextBrush"][theme], drawn);
                    if (glyph < 3.0) problems.Add($"{hue} glyph on the tinted {face} ({name}): {glyph:0.00}:1");
                    if (caption < 4.5) problems.Add($"TextBrush caption on the tinted {face} ({name}): {caption:0.00}:1");
                }
            }

            // The danger face of an irreversible act: its ink is text, and the face itself is a shape that
            // has to stand off the surface it sits on.
            var ink = IconographyConformanceTests.Contrast(palette["DangerTextBrush"][theme], palette["DangerBrush"][theme]);
            if (ink < 4.5) problems.Add($"DangerTextBrush on DangerBrush ({name}): {ink:0.00}:1");
            foreach (var surface in new[] { "WindowBackground", "SurfaceBrush", "ControlBrush" })
            {
                var edge = IconographyConformanceTests.Contrast(palette["DangerBrush"][theme], palette[surface][theme]);
                if (edge < 3.0) problems.Add($"DangerBrush face on {surface} ({name}): {edge:0.00}:1");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheTintIsTheContractsReferenceValue()
    {
        // 0.12 is a reference value of WINDOWS-UI 0.4 section 11 item 4; a change of it is a decision, so it
        // is named here rather than passing silently through the contrast gate above.
        Assert.Equal(0.12, Tint());
    }

    [Fact]
    public void EveryAnswerFaceTheMarkupNamesIsDerivedByTheThemeService()
    {
        var source = Assert.Single(AppSourceFile.LoadAll("ThemeService.cs")).Text;
        var prefixes = Regex.Matches(source, @"\(""(?<p>Answer\w+)"",\s*""\w+Brush""\)").Select(m => m.Groups["p"].Value).ToArray();
        var suffixes = Regex.Matches(source, @"\(""(?<s>\w*FaceBrush)"",\s*""\w+Brush""\)").Select(m => m.Groups["s"].Value).ToArray();
        Assert.NotEmpty(prefixes);
        Assert.Equal(3, suffixes.Length);
        var derived = prefixes.SelectMany(p => suffixes.Select(s => p + s)).ToHashSet(StringComparer.Ordinal);

        var used = AppSourceFile.LoadAll("*.xaml")
            .SelectMany(file => Regex.Matches(file.Text, @"\{DynamicResource\s+(?<key>Answer\w+)\s*\}").Select(m => m.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(used);
        Assert.Empty(used.Except(derived));
        Assert.Empty(derived.Except(used));
    }

    [Fact]
    public void EveryCommandButtonCarriesAGlyphOrIsADeclaredTextLink()
    {
        // WINDOWS-UI 0.4 section 11 item 1. A button draws a glyph through a glyph style (its own or one it
        // is based on), through an inline path of the generated dictionary, or it is one of the declared
        // exceptions below - each with the reason it is not a command button with a face.
        var styles = Styles();
        var problems = new List<string>();
        foreach (var file in AppSourceFile.LoadAll("*.xaml").Where(f => !f.Name.StartsWith("Localization.", StringComparison.Ordinal)
                     && f.Name is not "App.xaml" and not "Glyphs.xaml"))
        {
            var document = XDocument.Parse(file.Text);
            foreach (var button in document.Descendants().Where(e => e.Name.LocalName == "Button"))
            {
                if (HasGlyph(button, styles) || IsDeclaredLink(button)) continue;
                var name = button.Attribute(Xaml + "Name")?.Value ?? button.Attribute("Content")?.Value ?? "(unnamed)";
                problems.Add($"{file.Name}: {name} has neither a glyph style nor a declared reason");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryNoActionExitOfADialogIsDrawnAsAnAnswer()
    {
        // Item 4, and item 7 for what does not move: the dismissing answer (IsCancel) of every dialog the
        // product draws is a dismissing answer style - the glyph in the danger hue - and Enter's button is an
        // answer style or keeps the accent fill (item 7, recorded in the registry row).
        var problems = new List<string>();
        foreach (var file in AppSourceFile.LoadAll("*.xaml").Where(f => !f.Name.StartsWith("Localization.", StringComparison.Ordinal)
                     && f.Name is not "App.xaml" and not "Glyphs.xaml" && !NotDialogs.Contains(f.Name)))
        {
            foreach (var button in XDocument.Parse(file.Text).Descendants().Where(e => e.Name.LocalName == "Button"))
            {
                var key = StyleKey(button) ?? "";
                var label = button.Attribute(Xaml + "Name")?.Value ?? button.Attribute("Content")?.Value ?? "(unnamed)";
                if (button.Attribute("IsCancel")?.Value == "True" && !Regex.IsMatch(key, @"^(Cancel|Close)(Untinted)?AnswerGlyphButton$"))
                {
                    problems.Add($"{file.Name}: {label} is the no-action exit and is drawn with {key}");
                }

                if (button.Attribute("IsDefault")?.Value == "True"
                    && !key.EndsWith("AnswerGlyphButton", StringComparison.Ordinal)
                    && !(button.Attribute("Background")?.Value.Contains("AccentBrush", StringComparison.Ordinal) ?? false)
                    && !Regex.IsMatch(key, @"^(Cancel|Close)"))
                {
                    problems.Add($"{file.Name}: {label} holds Enter and is neither an answer nor an accent fill");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ADialogThatCanShowAnIrreversibleActDrawsItsDismissingAnswerUntinted()
    {
        // Item 7: in a window with a danger face the dismissing answer keeps its red glyph and takes no tint,
        // so the only red face is the one that cannot be undone.
        var problems = new List<string>();
        foreach (var file in AppSourceFile.LoadAll("*.cs").Where(f => f.Text.Contains("DeleteAnswerGlyphButton", StringComparison.Ordinal)))
        {
            var xaml = file.Name.Replace(".xaml.cs", ".xaml", StringComparison.Ordinal);
            var window = AppSourceFile.LoadAll("*.xaml").Single(f => f.Name == xaml);
            var cancel = XDocument.Parse(window.Text).Descendants()
                .Single(e => e.Name.LocalName == "Button" && e.Attribute("IsCancel")?.Value == "True");
            if (StyleKey(cancel) != "CancelUntintedAnswerGlyphButton")
            {
                problems.Add($"{xaml}: its no-action exit is {StyleKey(cancel)} beside an irreversible act");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // ------------------------------------------------------------------------------------------ data

    private static double Tint()
    {
        var source = Assert.Single(AppSourceFile.LoadAll("ThemeService.cs")).Text;
        var match = Regex.Match(source, @"const\s+double\s+AnswerTint\s*=\s*(?<v>[0-9.]+)\s*;");
        Assert.True(match.Success, "ThemeService.AnswerTint was not found.");
        return double.Parse(match.Groups["v"].Value, CultureInfo.InvariantCulture);
    }

    private static (int R, int G, int B) Blend((int R, int G, int B) hue, (int R, int G, int B) face, double share) => (
        (int)Math.Round(hue.R * share + face.R * (1 - share), MidpointRounding.AwayFromZero),
        (int)Math.Round(hue.G * share + face.G * (1 - share), MidpointRounding.AwayFromZero),
        (int)Math.Round(hue.B * share + face.B * (1 - share), MidpointRounding.AwayFromZero));

    /// <summary>Every keyed style: the key, the style it is based on, and whether it sets a glyph geometry.</summary>
    private static Dictionary<string, (string? BasedOn, bool SetsGlyph)> Styles()
    {
        var styles = new Dictionary<string, (string?, bool)>(StringComparer.Ordinal);
        foreach (var file in AppSourceFile.LoadAll("*.xaml").Where(f => !f.Name.StartsWith("Localization.", StringComparison.Ordinal)))
        {
            foreach (var style in XDocument.Parse(file.Text).Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                var key = style.Attribute(Xaml + "Key")?.Value;
                if (key is null) continue;
                var basedOn = StaticKey.Match(style.Attribute("BasedOn")?.Value ?? "");
                var setsGlyph = style.Elements().Any(e => e.Name.LocalName == "Setter"
                    && e.Attribute("Property")?.Value == "local:Glyph.Geometry");
                styles[key] = (basedOn.Success ? basedOn.Groups["key"].Value : null, setsGlyph);
            }
        }

        return styles;
    }

    private static string? StyleKey(XElement button)
    {
        var attribute = StaticKey.Match(button.Attribute("Style")?.Value ?? "");
        return attribute.Success ? attribute.Groups["key"].Value : null;
    }

    private static bool HasGlyph(XElement button, Dictionary<string, (string? BasedOn, bool SetsGlyph)> styles)
    {
        // An inline path of the generated dictionary inside the button's own content.
        if (button.Descendants().Any(e => e.Name.LocalName == "Path"
                && (e.Attribute("Data")?.Value.Contains("StaticResource Icon.", StringComparison.Ordinal) ?? false)))
        {
            return true;
        }

        var key = StyleKey(button);
        if (key is null)
        {
            // A local style written inside the button, based on a glyph style.
            var inline = button.Elements().FirstOrDefault(e => e.Name.LocalName == "Button.Style")?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Style");
            key = inline is null ? null : StaticKey.Match(inline.Attribute("BasedOn")?.Value ?? "").Groups["key"].Value;
        }

        for (var depth = 0; key is not null && depth < 8; depth++)
        {
            if (!styles.TryGetValue(key, out var style)) return false;
            if (style.SetsGlyph) return true;
            key = style.BasedOn;
        }

        return false;
    }

    /// <summary>
    /// Buttons that are not a command button with a face, so item 1 does not apply. Each is declared by the
    /// style that makes it so, and the reason is the style's own purpose.
    /// </summary>
    private static bool IsDeclaredLink(XElement button) =>
        // Hyperlink-looking text in the About page (licence, instructions, site..): a link, not a command.
        StyleKey(button) == "SettingsLinkButton"
        // The disclosure header of a main-window section: it carries its expand / collapse glyph inline.
        || StyleKey(button) == "SectionHeaderButtonStyle";
}
