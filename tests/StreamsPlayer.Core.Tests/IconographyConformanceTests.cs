using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0113: this product as a consumer of <c>ICON-SET</c>, <c>ICON-RENDER</c> and <c>ICON-EXTERNAL</c>.
/// </summary>
/// <remarks>
/// The contract store is not part of a clone, so nothing here reads it. What is held is this product's side:
/// <c>Glyphs.map.json</c> says what every glyph means, <c>assets/glyphs/</c> holds the vocabulary's drawings
/// as copied, and <c>Glyphs.xaml</c> is generated from both. These tests hold the three against each other
/// and against the markup - the inventory is complete and every entry is used once, no drawing lives outside
/// the generated dictionary, the copies are the ones the provenance names, English names carry the canonical
/// word, the colour roles reach 3:1 on every theme surface, the glyph never mirrors unless its meaning does,
/// and the site draws the same files. <c>tools/Sync-IconGlyphs.ps1 -Check</c> is the other half: it compares
/// the copies with the catalog where the catalog is mounted.
/// </remarks>
public sealed class IconographyConformanceTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Regex StaticReference = new(@"\{StaticResource\s+(?<key>(?:Icon|Chrome|Illustration)\.[\w.\-]+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex SiteGlyph = new(@"(?:\{\{|\[\[)glyph:(?<id>[\w.\-]+)(?:\}\}|\]\])", RegexOptions.Compiled);
    private static readonly Regex SvgPathData = new(@"<path[^>]*\sd=""(?<d>[^""]+)""", RegexOptions.Compiled);

    [Fact]
    public void EveryGlyphStyleIsInTheMapExactlyOnce()
    {
        var styles = GlyphStyles();
        var mapped = Map().Entries.SelectMany(entry => entry.Surfaces)
            .Where(surface => surface.StartsWith("style:", StringComparison.Ordinal))
            .Select(surface => surface["style:".Length..])
            .ToList();

        var duplicated = mapped.GroupBy(key => key).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        Assert.True(duplicated.Length == 0, "Mapped more than once: " + string.Join(", ", duplicated));
        Assert.Equal(styles.Keys.Order(StringComparer.Ordinal), mapped.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryGlyphStyleDrawsTheGeometryOfItsMappedMeaning()
    {
        var styles = GlyphStyles();
        var problems = new List<string>();
        foreach (var entry in Map().Entries)
        {
            foreach (var surface in entry.Surfaces.Where(surface => surface.StartsWith("style:", StringComparison.Ordinal)))
            {
                var key = surface["style:".Length..];
                if (styles.TryGetValue(key, out var geometry) && geometry != entry.GeometryKey)
                {
                    problems.Add($"{key} draws {geometry}, the map says {entry.GeometryKey}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryGeometryIsClassifiedAndEveryReferenceResolves()
    {
        var map = Map();
        var generated = GeometryKeys();
        var expected = map.Entries.Where(entry => entry.InApplication).Select(entry => entry.GeometryKey)
            .Concat(map.Private)
            .Order(StringComparer.Ordinal);
        Assert.Equal(expected, generated.Order(StringComparer.Ordinal));

        var referenced = AppSourceFile.LoadAll("*.xaml").Concat(AppSourceFile.LoadAll("*.cs"))
            .Where(file => file.Name != "Glyphs.xaml")
            .SelectMany(file => StaticReference.Matches(file.Text).Select(match => match.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(referenced.Except(generated));
        Assert.Empty(generated.Except(referenced));
    }

    [Fact]
    public void NoGlyphIsDrawnOutsideTheGeneratedDictionary()
    {
        var literal = new Regex(@"Data=""\s*[MmFf]|Figures=""|<(?:Path|Stream)?Geometry[\s>]|Geometry\.Parse\(", RegexOptions.Compiled);
        var problems = AppSourceFile.LoadAll("*.xaml").Concat(AppSourceFile.LoadAll("*.cs"))
            .Where(file => file.Name != "Glyphs.xaml")
            .SelectMany(file => literal.Matches(file.Text).Select(match => $"{file.Name}:{LineOf(file.Text, match.Index)}"))
            .ToList();

        Assert.True(problems.Count == 0, "A drawing outside Glyphs.xaml: " + string.Join(", ", problems));
    }

    [Fact]
    public void TheVendoredCopiesAreTheOnesTheProvenanceNamesAndTheGeometriesAreTheirs()
    {
        var map = Map();
        var provenance = File.ReadAllLines(Path.Combine(GlyphDirectory, "PROVENANCE.txt"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split("  ", 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        var header = File.ReadAllLines(Path.Combine(GlyphDirectory, "PROVENANCE.txt")).Single(line => line.StartsWith("# ICON-SET", StringComparison.Ordinal));
        Assert.Contains($"ICON-SET {map.Contracts["ICON-SET"]},", header, StringComparison.Ordinal);

        var figures = GeneratedFigures();
        var problems = new List<string>();
        foreach (var entry in map.Entries)
        {
            var copy = Path.Combine(GlyphDirectory, entry.Pending ? "pending" : "", entry.File + ".svg");
            // A pending meaning only the site shows (the theme switch) has no drawing of this product's yet.
            if (entry.Pending && !entry.InApplication) continue;
            if (!File.Exists(copy)) { problems.Add($"no drawing for {entry.Id}"); continue; }

            if (!entry.Pending)
            {
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copy)));
                if (!provenance.TryGetValue(entry.Id + ".svg", out var recorded) || recorded != hash)
                {
                    problems.Add($"{entry.Id}.svg is not the copy PROVENANCE.txt records");
                }
            }

            if (!entry.InApplication) continue;
            var own = Path.Combine(GlyphDirectory, "own", entry.File + ".svg");
            var drawn = !entry.Pending && File.Exists(own) ? own : copy;
            var expected = SvgPathData.Matches(File.ReadAllText(drawn)).Select(match => match.Groups["d"].Value).ToArray();
            if (!figures.TryGetValue(entry.GeometryKey, out var actual) || !expected.SequenceEqual(actual))
            {
                problems.Add($"{entry.GeometryKey} in Glyphs.xaml is not the drawing of {Path.GetRelativePath(GlyphDirectory, drawn)}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EnglishNamesCarryTheCanonicalName()
    {
        // ICON-SET rule 3: a label may qualify the name with its object ("Previous station"), never
        // substitute another word for it. Checked in English, where word order leaves every canonical word
        // intact; the inflected languages are read by a person against the record (SP-0113 plan, phase 4).
        var english = LocalizationDictionary.Load(Path.Combine(LocalizationDictionary.Directory, "Localization.en.xaml")).Values;
        var problems = new List<string>();
        foreach (var entry in Map().Entries)
        {
            foreach (var key in entry.NameKeys)
            {
                if (!english.TryGetValue(key, out var value)) { problems.Add($"{key}: no such string"); continue; }
                var words = Words(value);
                var missing = Words(entry.Name).Where(word => !words.Contains(word)).ToArray();
                if (missing.Length > 0) problems.Add($"{key} = \"{value}\" names {entry.Id} without \"{string.Join(' ', missing)}\"");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void GlyphColourRolesReachThreeToOneOnEveryThemeSurface()
    {
        // ICON-RENDER 3. The roles a glyph is painted in, on every surface a glyph sits on, in both themes.
        // The pressed fills are left out: they last as long as the mouse button is down and are feedback,
        // not a surface a glyph is read on.
        var palette = Palette();
        string[] roles = ["TextBrush", "MutedBrush", "DangerBrush"];
        string[] surfaces = ["WindowBackground", "SurfaceBrush", "ControlBrush", "ControlHoverBrush", "CardBrush", "CardSelectedBrush", "SectionBrush"];
        var problems = new List<string>();
        for (var theme = 0; theme < 2; theme++)
        {
            foreach (var role in roles)
            {
                foreach (var surface in surfaces)
                {
                    Check(role, surface, palette[role][theme], palette[surface][theme], theme);
                }
            }

            Check("AccentTextBrush", "AccentBrush", palette["AccentTextBrush"][theme], palette["AccentBrush"][theme], theme);
        }

        // The player's overlay is out of theme (APP-STYLE 5): white and the stop-recording red on its fills.
        foreach (var (name, fill) in new[] { ("overlay", "#FF141A24"), ("overlay hover", "#FF27303D") })
        {
            Check("White", name, Hex("#FFFFFFFF"), Hex(fill), 0);
            Check("#FF4D4D", name, Hex("#FFFF4D4D"), Hex(fill), 0);
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        void Check(string role, string surface, (int R, int G, int B) glyph, (int R, int G, int B) under, int theme)
        {
            var ratio = Contrast(glyph, under);
            if (ratio < 3.0) problems.Add($"{role} on {surface} ({(theme == 0 ? "light" : "dark")}): {ratio:0.00}:1");
        }
    }

    [Fact]
    public void AGlyphMirrorsOnlyWhenItsMeaningDoes()
    {
        // ICON-RENDER 7 and APP-BEHAVIOUR 8: every glyph template pins its drawing left-to-right, except the
        // templates of the meanings the vocabulary marks rtl: mirror.
        var mirrored = Map().Entries.Where(entry => entry.Mirror)
            .SelectMany(entry => entry.Surfaces)
            .Where(surface => surface.StartsWith("style:", StringComparison.Ordinal))
            .Select(surface => surface["style:".Length..])
            .ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var file in AppSourceFile.LoadAll("*.xaml").Where(file => file.Name != "Glyphs.xaml"))
        {
            var document = XDocument.Parse(file.Text, LoadOptions.SetLineInfo);
            foreach (var path in document.Descendants().Where(element => element.Name.LocalName == "Path"))
            {
                var data = path.Attribute("Data")?.Value ?? "";
                if (!data.Contains("Glyph.Geometry", StringComparison.Ordinal) && !StaticReference.IsMatch(data)) continue;
                if (data.Contains("Chrome.", StringComparison.Ordinal) || data.Contains("Illustration.", StringComparison.Ordinal)) continue;

                var style = path.Ancestors().FirstOrDefault(element => element.Name.LocalName == "Style")?.Attribute(Xaml + "Key")?.Value;
                var pinned = path.AncestorsAndSelf().Any(element => element.Attribute("FlowDirection")?.Value == "LeftToRight");
                var shouldMirror = style is not null && mirrored.Contains(style);
                if (pinned == shouldMirror)
                {
                    problems.Add($"{file.Name}:{((System.Xml.IXmlLineInfo)path).LineNumber} {(shouldMirror ? "is pinned but its meaning mirrors" : "is not pinned left-to-right")}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheSiteDrawsTheVendoredGlyphsAndNoSymbolStandsInForOne()
    {
        var map = Map();
        var known = map.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var templates = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "site-templates"), "*.html");
        var decks = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "site-copy"), "*.txt");
        var problems = new List<string>();

        foreach (var path in templates.Concat(decks))
        {
            var text = File.ReadAllText(path);
            foreach (Match match in SiteGlyph.Matches(text))
            {
                if (!known.Contains(match.Groups["id"].Value)) problems.Add($"{Path.GetFileName(path)}: unknown glyph {match.Value}");
            }

            // PAGE-STYLE's back-to-top arrow and the kit's arrows are substitutes for vocabulary glyphs.
            foreach (var symbol in new[] { "↑", "⤓" })
            {
                if (text.Contains(symbol, StringComparison.Ordinal)) problems.Add($"{Path.GetFileName(path)} draws {symbol}");
            }

            // The theme switch keeps the kit's symbol only while its meaning is pending in the vocabulary.
            if (text.Contains('◐') && !map.Entries.Any(entry => entry.Pending && entry.Id == "app.theme"))
            {
                problems.Add($"{Path.GetFileName(path)} draws ◐ although app.theme is no longer pending");
            }
        }

        var footer = File.ReadAllText(templates.Single(path => Path.GetFileName(path) == "_footer.html"));
        Assert.Contains("{{glyph:nav.scroll-top}}", footer, StringComparison.Ordinal);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheGatesActuallyFoundTheInventory()
    {
        // Floors well under what ships on 2026-09-24, so a derivation that stopped matching fails loudly.
        Assert.True(GlyphStyles().Count >= 40, $"Only {GlyphStyles().Count} glyph styles were found.");
        Assert.True(GeometryKeys().Count >= 50, $"Only {GeometryKeys().Count} geometries were found.");
        Assert.True(Map().Entries.Count(entry => !entry.Pending) >= 35);
    }

    // ------------------------------------------------------------------------------------------ data

    private static string GlyphDirectory => Path.Combine(AppContext.BaseDirectory, "glyphs");

    private sealed record MapEntry(string Id, string Name, bool Pending, bool Mirror, bool InApplication,
        IReadOnlyList<string> Surfaces, IReadOnlyList<string> NameKeys)
    {
        public string GeometryKey => Pending ? $"Icon.pending.{Id}" : $"Icon.{Id}";

        public string File => Id;
    }

    private sealed record IconMap(IReadOnlyDictionary<string, string> Contracts, IReadOnlyList<MapEntry> Entries, IReadOnlyList<string> Private);

    private static IconMap Map()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppSourceFile.Directory, "Glyphs.map.json")));
        var root = document.RootElement;
        var contracts = root.GetProperty("contracts").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var entries = new List<MapEntry>();
        foreach (var (property, pending) in new[] { ("meanings", false), ("pending", true) })
        {
            foreach (var item in root.GetProperty(property).EnumerateArray())
            {
                var id = item.GetProperty(pending ? "key" : "id").GetString()!;
                var surfaces = item.GetProperty("surfaces").EnumerateArray().Select(s => s.GetString()!).ToArray();
                var site = item.TryGetProperty("site", out var flag) && flag.GetBoolean();
                var inApplication = !site && surfaces.Any(surface => !surface.StartsWith("site ", StringComparison.Ordinal));
                var mirror = item.TryGetProperty("rtl", out var rtl) && rtl.GetString() == "mirror";
                var names = item.TryGetProperty("nameKeys", out var keys) ? keys.EnumerateArray().Select(k => k.GetString()!).ToArray() : [];
                entries.Add(new MapEntry(id, item.GetProperty("name").GetString()!, pending, mirror, inApplication, surfaces, names));
            }
        }

        var privateKeys = root.GetProperty("private").EnumerateArray().Select(item => item.GetProperty("key").GetString()!).ToArray();
        return new IconMap(contracts, entries, privateKeys);
    }

    /// <summary>Every style that sets a glyph, with the geometry key it sets.</summary>
    private static Dictionary<string, string> GlyphStyles()
    {
        var styles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in AppSourceFile.LoadAll("*.xaml"))
        {
            foreach (var style in XDocument.Parse(file.Text).Descendants().Where(element => element.Name.LocalName == "Style"))
            {
                var key = style.Attribute(Xaml + "Key")?.Value;
                var setter = style.Elements().FirstOrDefault(element =>
                    element.Name.LocalName == "Setter" && element.Attribute("Property")?.Value == "local:Glyph.Geometry");
                if (key is null || setter is null) continue;
                var match = StaticReference.Match(setter.Attribute("Value")?.Value ?? "");
                styles[key] = match.Success ? match.Groups["key"].Value : "(not a StaticResource)";
            }
        }

        return styles;
    }

    private static HashSet<string> GeometryKeys() =>
        GeneratedDocument().Root!.Elements()
            .Select(element => element.Attribute(Xaml + "Key")?.Value)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string[]> GeneratedFigures() =>
        GeneratedDocument().Root!.Elements()
            .Where(element => element.Attribute(Xaml + "Key") is not null)
            .ToDictionary(
                element => element.Attribute(Xaml + "Key")!.Value,
                element => element.DescendantsAndSelf().Select(e => e.Attribute("Figures")?.Value).OfType<string>().ToArray(),
                StringComparer.Ordinal);

    private static XDocument GeneratedDocument() =>
        XDocument.Parse(Assert.Single(AppSourceFile.LoadAll("Glyphs.xaml")).Text);

    private static HashSet<string> Words(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    /// <summary>ThemeService's palette table: each role's light and dark colour.</summary>
    private static Dictionary<string, (int R, int G, int B)[]> Palette()
    {
        var source = Assert.Single(AppSourceFile.LoadAll("ThemeService.cs")).Text;
        var colour = new Regex(@"Colors\.(?<name>White|Black)|Color\.FromRgb\((?<r>\d+),\s*(?<g>\d+),\s*(?<b>\d+)\)|Color\.FromArgb\((?<a>\d+),\s*(?<ar>\d+),\s*(?<ag>\d+),\s*(?<ab>\d+)\)");
        var palette = new Dictionary<string, (int R, int G, int B)[]>(StringComparer.Ordinal);
        foreach (Match entry in Regex.Matches(source, @"\[\s*""(?<key>\w+)""\s*\]\s*=\s*\((?<pair>.+?)\)\s*,?\s*\r?\n"))
        {
            var values = colour.Matches(entry.Groups["pair"].Value).Select(ParseColour).ToArray();
            if (values.Length == 2) palette[entry.Groups["key"].Value] = values;
        }

        Assert.True(palette.Count >= 15, $"Only {palette.Count} palette roles were read from ThemeService.cs.");
        return palette;

        static (int, int, int) ParseColour(Match match) => match.Groups["name"].Success
            ? match.Groups["name"].Value == "White" ? (255, 255, 255) : (0, 0, 0)
            : match.Groups["r"].Success
                ? (int.Parse(match.Groups["r"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["g"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["b"].Value, CultureInfo.InvariantCulture))
                : (int.Parse(match.Groups["ar"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["ag"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["ab"].Value, CultureInfo.InvariantCulture));
    }

    private static (int R, int G, int B) Hex(string argb) => (
        int.Parse(argb.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(argb.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(argb.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static double Contrast((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);

        static double Luminance((int R, int G, int B) c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

        static double Channel(int value)
        {
            var v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
    }
}
