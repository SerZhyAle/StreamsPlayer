using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0105: the shared <c>INSTALL-TRUST</c> contract, as far as it is readable from the page itself -
/// which is exactly what the contract's conformance section says a product is judged on.
/// <para>
/// Rule 1 is the section order (what the warning is, why it appears, what to click, what the app never
/// does); rule 2 is the quoted dialog; rule 4 is the absence of any instruction that weakens a protection.
/// The template and the copy decks come in as linked data, never loaded, the same way the localization
/// gates read the App's dictionaries.
/// </para>
/// </summary>
public sealed partial class InstallTrustPageTests
{
    private const string SmartScreenHeading = "Windows protected your PC";
    private const string ElevationHeading = "Do you want to allow this app from an unknown publisher to make changes to your device?";

    private static readonly string[] SectionIdsInOrder = ["trust-what", "trust-why", "trust-click", "trust-never"];

    [Fact]
    public void TemplateCarriesTheFourSectionsInContractOrder()
    {
        var template = ReadTemplate();

        var positions = SectionIdsInOrder
            .Select(id => (Id: id, Index: template.IndexOf($"id=\"{id}\"", StringComparison.Ordinal)))
            .ToArray();

        Assert.All(positions, section => Assert.True(section.Index >= 0, $"trust.html has no section id=\"{section.Id}\""));
        Assert.Equal(positions.OrderBy(section => section.Index).Select(section => section.Id), SectionIdsInOrder);
    }

    [Fact]
    public void EveryTemplateKeyExistsInEveryDeck()
    {
        var keys = TemplateKeyPattern().Matches(ReadTemplate()).Select(match => match.Groups[1].Value).Distinct().ToArray();
        Assert.Contains("trust-click-1", keys);

        foreach (var (code, deck) in ReadDecks())
        {
            var missing = keys.Where(key => !deck.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)).ToArray();
            Assert.True(missing.Length == 0, $"[{code}] trust page key(s) missing or empty: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void EnglishDeckQuotesBothDialogsVerbatim()
    {
        var english = ReadDecks()["en"];

        Assert.Contains(SmartScreenHeading, english["trust-what-title"], StringComparison.Ordinal);
        Assert.Contains(SmartScreenHeading, english["trust-click-intro"], StringComparison.Ordinal);
        Assert.Contains(ElevationHeading, english["trust-admin-title"], StringComparison.Ordinal);
        Assert.Contains("More info", english["trust-click-1"], StringComparison.Ordinal);
        Assert.Contains("Run anyway", english["trust-click-2"], StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDeckLetsAnEnglishWindowsUserMatchTheHeading()
    {
        // A visitor reads the page in one language and may meet the dialog in another; the English heading
        // somewhere in the first section is what lets them match what is on screen.
        foreach (var (code, deck) in ReadDecks())
        {
            var firstSection = deck["trust-what-title"] + "\n" + deck["trust-what-1"];
            Assert.True(firstSection.Contains(SmartScreenHeading, StringComparison.Ordinal),
                $"[{code}] the first trust section never names the English heading \"{SmartScreenHeading}\"");
        }
    }

    [Fact]
    public void EnglishDeckGivesNoInstructionThatWeakensAProtection()
    {
        var english = ReadDecks()["en"];
        var trustText = string.Join("\n", english.Where(entry => entry.Key.StartsWith("trust-", StringComparison.Ordinal)).Select(entry => entry.Value));

        var offending = ForbiddenInstructionPattern().Matches(trustText).Select(match => match.Value.Trim()).ToArray();
        Assert.True(offending.Length == 0, $"rule 4 forbids these instructions: {string.Join(" | ", offending)}");
    }

    [Fact]
    public void ForbiddenInstructionPatternCatchesAnImperativeButNotANegation()
    {
        // The gate above passes quietly when nothing matches, so prove it can fail.
        Assert.Matches(ForbiddenInstructionPattern(), "It is safe. Turn off SmartScreen for a moment.");
        Assert.Matches(ForbiddenInstructionPattern(), "Disable your antivirus, then run it.");
        Assert.Matches(ForbiddenInstructionPattern(), "Right-click the file: run as administrator.");
        Assert.DoesNotMatch(ForbiddenInstructionPattern(), "You never need to switch SmartScreen off or disable your antivirus.");
        Assert.DoesNotMatch(ForbiddenInstructionPattern(), "An exception is enough; do not turn your antivirus off.");
    }

    private static string ReadTemplate() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "site-templates", "trust.html"));

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadDecks()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "site-copy"), "*.txt");
        Assert.NotEmpty(files);
        return files.ToDictionary(
            path => Path.GetFileNameWithoutExtension(path),
            path => ParseDeck(File.ReadAllText(path)));
    }

    // The deck format of tools/site/build-site.ps1: an "@@key" line, then the value until the next key.
    private static IReadOnlyDictionary<string, string> ParseDeck(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? key = null;
        var buffer = new List<string>();

        void Flush()
        {
            if (key is not null)
            {
                values[key] = string.Join("\n", buffer).Trim();
            }
        }

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                key = line[2..].Trim();
                buffer.Clear();
                continue;
            }

            if (key is not null)
            {
                buffer.Add(line);
            }
        }

        Flush();
        return values;
    }

    [GeneratedRegex(@"\{\{t\.(trust-[a-z0-9-]+)\}\}")]
    private static partial Regex TemplateKeyPattern();

    // An imperative at the start of a sentence or clause - the shape an instruction takes. The same words
    // after "never need to" or "do not" are the page saying the opposite, which rule 4 welcomes.
    [GeneratedRegex(@"(^|[.!?;:]\s+)(turn off|switch off|disable|deactivate|pause|run (it |the \w+ )?as administrator)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ForbiddenInstructionPattern();
}
