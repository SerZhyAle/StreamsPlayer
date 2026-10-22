using System.Security.Cryptography;
using StreamsPlayer.App;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0234: this product as a consumer of <c>EXCHANGE-MARK</c> (rungs 2 and 3 of its section 5, as far as a clone
/// can hold them). The contract store is not part of a clone, so nothing here reads it: what is held is this
/// product's side - the vendored files are the ones <c>assets/exchange-mark/PROVENANCE.txt</c> records, the size a
/// slot takes at a screen scale is a step the set holds and is never scaled up, and the picture stands on exactly
/// the surfaces the ticket's inventory lists and on no other. <c>tools/Sync-ExchangeMark.ps1 -Check</c> is the other
/// half: it compares the copies with the catalog where the catalog is mounted.
/// </summary>
public sealed class ExchangeMarkConformanceTests
{
    private static string Vendored => Path.Combine(AppContext.BaseDirectory, "exchange-mark");

    private static string OwnGlyph => Path.Combine(AppContext.BaseDirectory, "glyphs", "own", "source.exchange-server.svg");

    /// <summary>The recorded files: contract path, SHA-256 (lower case) and the path in this repository.</summary>
    private static IReadOnlyList<(string Contract, string Hash, string Repository)> Provenance() =>
    [
        .. File.ReadAllLines(Path.Combine(Vendored, "PROVENANCE.txt"))
            .Where(line => line.Length > 0 && line[0] != '#')
            .Select(line => line.Split("  ", 3))
            .Select(parts => (parts[0], parts[1], parts[2]))
    ];

    [Fact]
    public void EveryVendoredCopyIsTheFileTheProvenanceRecords()
    {
        var problems = new List<string>();
        foreach (var (contract, hash, repository) in Provenance())
        {
            var file = repository.StartsWith("assets/exchange-mark/", StringComparison.Ordinal)
                ? Path.Combine(Vendored, repository["assets/exchange-mark/".Length..])
                : OwnGlyph;
            if (!File.Exists(file)) { problems.Add($"{contract}: {repository} is not in the repository"); continue; }
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            if (actual != hash) { problems.Add($"{repository} is not {contract} of the recorded version (rule 6: a copy is never edited)"); }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ThePngStepsOnDiskAreExactlyTheStepsTheApplicationPicksFrom()
    {
        var onDisk = Directory.GetFiles(Path.Combine(Vendored, "png"), "mark-*.png")
            .Select(path => int.Parse(Path.GetFileNameWithoutExtension(path)["mark-".Length..]))
            .Order()
            .ToArray();
        Assert.Equal(ExchangeMarkSteps.Held, onDisk);

        // The files the application compiles in are the files the provenance pins, no more and no fewer.
        var recorded = Provenance().Where(entry => entry.Contract.StartsWith("png/", StringComparison.Ordinal))
            .Select(entry => entry.Contract).Order(StringComparer.Ordinal);
        Assert.Equal(ExchangeMarkSteps.Held.Select(step => $"png/mark-{step}.png").Order(StringComparer.Ordinal), recorded);
    }

    [Theory]
    // Rule 5: the set holds the 100 to 200 per cent steps of 16 and 24; each is the exact frame, so nothing is scaled.
    [InlineData(16, 1.00, 16)]
    [InlineData(16, 1.25, 20)]
    [InlineData(16, 1.50, 24)]
    [InlineData(16, 1.75, 28)]
    [InlineData(16, 2.00, 32)]
    [InlineData(24, 1.00, 24)]
    [InlineData(24, 1.25, 30)]
    [InlineData(24, 1.50, 36)]
    [InlineData(24, 1.75, 42)]
    [InlineData(24, 2.00, 48)]
    // A scale between two steps takes the next one up in pixels and the platform scales it DOWN, never up.
    [InlineData(16, 1.10, 20)]
    [InlineData(24, 1.10, 28)]
    // Larger scales stay inside the set.
    [InlineData(16, 2.50, 40)]
    [InlineData(24, 3.00, 96)]
    public void ASlotTakesTheSmallestHeldStepThatIsNotSmallerThanItsPixels(double slot, double scale, int expected) =>
        Assert.Equal(expected, ExchangeMarkSteps.For(slot, scale));

    [Fact]
    public void ANeverScaledUpStepIsNeverSmallerThanTheSlotWhileTheSetReaches()
    {
        var problems = new List<string>();
        foreach (var slot in new[] { 16.0, 24.0 })
        {
            for (var scale = 1.0; scale <= 3.0; scale += 0.05)
            {
                var step = ExchangeMarkSteps.For(slot, scale);
                var pixels = slot * scale;
                if (step < pixels - 1e-6 && step != ExchangeMarkSteps.Held[^1])
                {
                    problems.Add($"{slot} DIP at {scale:0.00} needs {pixels:0.0} px and would be given {step}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheMarkStandsOnTheSurfacesTheTicketListsAndOnNoOther()
    {
        // R1, R2, R3, R5, R6 and R10 carry the coloured mark; R4 carries the one-tone glyph through the glyph map.
        // R7 (message boxes), R8, R9, R12, R16, R17 and R19 carry none, so no other source may mention the control.
        var expected = new[]
        {
            "BroadcastsWindow.xaml", "ExchangeMarkIcon.cs", "ExchangePasswordWindow.xaml", "SettingsWindow.Exchange.cs", "SettingsWindow.xaml"
        };
        var holders = Directory.GetFiles(AppSourceFile.Directory)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(path => !Path.GetFileName(path).StartsWith("Localization.", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("ExchangeMarkIcon", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, holders);
    }

    [Fact]
    public void TheRelayGlyphKeepsTheServerBaysAndOnlyTheStatusRowWearsTheOneToneGlyph()
    {
        // EXCHANGE-MARK section 6: relaying a stream is something the server does, not the server, so
        // feature.server-relay never wears the mark; the status row source.exchange-server does, as the one-tone glyph.
        var glyphs = Path.Combine(AppContext.BaseDirectory, "glyphs");
        Assert.False(File.Exists(Path.Combine(glyphs, "own", "feature.server-relay.svg")));
        Assert.True(File.Exists(OwnGlyph));
        var settings = File.ReadAllText(Path.Combine(AppSourceFile.Directory, "SettingsWindow.xaml"));
        Assert.Contains("Icon.feature.server-relay", settings, StringComparison.Ordinal);
        var main = File.ReadAllText(Path.Combine(AppSourceFile.Directory, "MainWindow.xaml"));
        Assert.Contains("Icon.source.exchange-server", main, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMarkIsNeverMirroredAndHasNoTextOrLikenessOfItsOwn()
    {
        // Rule 2: a right-to-left window mirrors its layout, not this picture, so the element pins its own direction;
        // rule 1: no drawing of the star in the application - the control takes the vendored PNG or the glyph's geometry.
        var control = File.ReadAllText(Path.Combine(AppSourceFile.Directory, "ExchangeMarkIcon.cs"));
        Assert.Contains("FlowDirection = FlowDirection.LeftToRight", control, StringComparison.Ordinal);
        Assert.DoesNotContain("DrawingContext", control, StringComparison.Ordinal);
        Assert.DoesNotContain("RotateTransform", control, StringComparison.Ordinal);
        Assert.DoesNotContain("ScaleTransform", control, StringComparison.Ordinal);
    }
}
