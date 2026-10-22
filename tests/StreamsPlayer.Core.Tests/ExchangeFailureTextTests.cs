using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0225 requirement 6: the family's words. The EN, RU and UK strings of every row this product raises and of the
/// fifteen steps equal the catalog's tables (<c>DEVICE-EXCHANGE</c> items CZ and CY) character for character, and the
/// other ten languages carry the same keys with the same placeholders. The vector is exported from the catalog by
/// <c>tools/localization/check-exchange-terms.ps1 -ExportFailureTable</c>, so CI holds the words without the catalog.
/// </summary>
public sealed class ExchangeFailureTextTests
{
    private static readonly Lazy<JsonDocument> Table = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "exchange-failure-table.json"))));

    private static readonly string[] ThreeLanguages = ["en", "ru", "uk"];

    private static IReadOnlyDictionary<string, string> Words(string code) =>
        LocalizationDictionary.Load(Path.Combine(LocalizationDictionary.Directory, $"Localization.{code}.xaml")).Values;

    [Fact]
    public void EveryRowThisProductRaisesIsInTheTableAndTheTableHoldsNoOtherRow()
    {
        var tableCodes = Table.Value.RootElement.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("code").GetString()!).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(ExchangeFailureRows.All.Select(row => row.Code).Order(StringComparer.Ordinal), tableCodes);
    }

    [Fact]
    public void EveryRowHasTheKindAndTheStepOfItsRowInTheTable()
    {
        foreach (var entry in Table.Value.RootElement.GetProperty("rows").EnumerateArray())
        {
            var code = entry.GetProperty("code").GetString()!;
            var row = ExchangeFailureRows.ByCode(code);
            Assert.NotNull(row);
            Assert.Equal(entry.GetProperty("kind").GetString(), row.Kind.ToString().ToLowerInvariant());
            var step = entry.GetProperty("step").GetString();
            Assert.Equal(step, row.Step is { } own ? ExchangeDiagnosisSteps.IdOf(own) : null);
        }
    }

    [Fact]
    public void EnglishRussianAndUkrainianRowsAreTheTablesVerbatim()
    {
        var problems = new List<string>();
        foreach (var language in ThreeLanguages)
        {
            var words = Words(language);
            foreach (var entry in Table.Value.RootElement.GetProperty("rows").EnumerateArray())
            {
                var row = ExchangeFailureRows.ByCode(entry.GetProperty("code").GetString())!;
                var expected = entry.GetProperty(language).GetString()!;
                if (!words.TryGetValue(row.MessageKey, out var actual) || !string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    problems.Add($"{language}/{row.MessageKey}: expected '{expected}', found '{actual}'");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EnglishRussianAndUkrainianStepsAreTheTablesVerbatimAndInTheTablesOrder()
    {
        var problems = new List<string>();
        var steps = Table.Value.RootElement.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(15, steps.Length);
        foreach (var entry in steps)
        {
            var step = (ExchangeDiagnosisStep)entry.GetProperty("number").GetInt32();
            Assert.Equal(entry.GetProperty("id").GetString(), ExchangeDiagnosisSteps.IdOf(step));
            Assert.Equal(entry.GetProperty("applies").GetString(), ExchangeDiagnosisSteps.AppliesOf(step).ToString().ToLowerInvariant());
            foreach (var language in ThreeLanguages)
            {
                var expected = entry.GetProperty(language).GetString()!;
                if (!Words(language).TryGetValue(ExchangeDiagnosisSteps.MessageKey(step), out var actual)
                    || !string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    problems.Add($"{language}/{ExchangeDiagnosisSteps.MessageKey(step)}: expected '{expected}', found '{actual}'");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void AMessageIsNeverJoinedWithOtherText()
    {
        // Item DA: the text of a row is shown unchanged. No row's text carries a placeholder or a sentence of the
        // product's own, so the text on screen is the table's text and nothing else.
        foreach (var language in ThreeLanguages)
        {
            var words = Words(language);
            foreach (var row in ExchangeFailureRows.All)
            {
                Assert.DoesNotMatch(new Regex(@"\{\d+\}"), words[row.MessageKey]);
            }
        }
    }

    [Fact]
    public void TheCastUnavailableTextIsTheUnavailableRowsText()
    {
        // The table has one `unavailable` row for the wire and for a cast; this product keeps a key for each.
        foreach (var dictionary in LocalizationDictionary.LoadAll())
        {
            Assert.Equal(dictionary.Values["ExchangeUnavailable"], dictionary.Values["BroadcastCastUnavailable"]);
        }
    }

    [Fact]
    public void EveryLanguageHoldsEveryRowAndStepWithNoPlaceholder()
    {
        var keys = ExchangeFailureRows.All.Select(row => row.MessageKey)
            .Concat(ExchangeDiagnosisSteps.All.Select(ExchangeDiagnosisSteps.MessageKey)).ToArray();
        foreach (var dictionary in LocalizationDictionary.LoadAll())
        {
            foreach (var key in keys)
            {
                Assert.True(dictionary.Values.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text),
                    $"{dictionary.Code}/{key} is missing");
                Assert.DoesNotMatch(new Regex(@"\{\d+\}"), text!);
            }
        }
    }

    [Fact]
    public void NoLanguageAsksForAnUpdateOfTheProgram()
    {
        // The contract has no "update the program" message in release 46 (items CK and CZ).
        var removed = new[]
        {
            "ExchangeStringUpdate", "BroadcastsUnsupportedSchema", "ExchangePasswordUnanswered", "FmsBroadcastUnsupportedSchema"
        };
        foreach (var dictionary in LocalizationDictionary.LoadAll())
        {
            foreach (var key in removed)
            {
                Assert.False(dictionary.Values.ContainsKey(key), $"{dictionary.Code}/{key} should be gone");
            }
        }
    }

    // The update verb of each language, which a text about the product is the request when it shares a value with the
    // product's name. A key list cannot catch a request written under a new key; the words can.
    private static readonly IReadOnlyDictionary<string, string> UpdateRequest = new Dictionary<string, string>
    {
        ["en"] = "Update", ["ru"] = "Обнови", ["uk"] = "Онови", ["zh"] = "更新", ["hi"] = "अपडेट", ["ur"] = "تازہ",
        ["de"] = "Aktualisier", ["fr"] = "à jour", ["bn"] = "হালনাগাদ", ["ar"] = "حدّث", ["pt"] = "Atualiz",
        ["es"] = "Actualic", ["it"] = "Aggiorna"
    };

    [Fact]
    public void NoExchangeOrBroadcastTextAsksForAnUpdateOfStreamsPlayer()
    {
        // A descriptor or a connection string of a higher version is the family's format-unsupported row, which
        // names no update (amendment of 2026-10-17); another text that asks for one under a new key is caught here.
        var problems = new List<string>();
        foreach (var dictionary in LocalizationDictionary.LoadAll())
        {
            var request = UpdateRequest[dictionary.Code];
            foreach (var (key, text) in dictionary.Values.Where(pair => IsExchangeFamilyKey(pair.Key)))
            {
                if (text.Contains("StreamsPlayer", StringComparison.Ordinal)
                    && text.Contains(request, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{dictionary.Code}/{key}");
                }
            }
        }

        Assert.True(problems.Count == 0, "Asks for an update: " + string.Join(", ", problems));
    }

    // BroadcastResource* is the `.spres` document of BROADCAST-RESOURCE, a contract of its own whose rule for a higher
    // schema is to reject it and which carries no ban on saying why; DEVICE-EXCHANGE item CK binds only this family.
    private static bool IsExchangeFamilyKey(string key) => !key.StartsWith("BroadcastResource", StringComparison.Ordinal)
        && (key.StartsWith("Exchange", StringComparison.Ordinal) || key.StartsWith("Broadcast", StringComparison.Ordinal)
            || key.StartsWith("FmsBroadcast", StringComparison.Ordinal));

    [Theory]
    [InlineData("MainWindow.FastMediaSorterBroadcast.cs", 1)]
    [InlineData("MainWindow.BroadcastWindows.cs", 2)]
    [InlineData("MainWindow.Exchange.cs", 1)]
    public void EveryDescriptorEntryPointShowsTheFormatUnsupportedRowForAHigherSchema(string file, int entryPoints)
    {
        var source = Assert.Single(AppSourceFile.LoadAll(file));

        Assert.DoesNotContain("FmsBroadcastUnsupportedSchema", source.Text, StringComparison.Ordinal);
        Assert.Equal(entryPoints, System.Text.RegularExpressions.Regex.Count(source.Text,
            @"UnsupportedSchema\s*(=>|\?)\s*ExchangeFailureRows\.FormatUnsupported\.MessageKey"));
    }

    [Theory]
    [InlineData("MainWindow.FastMediaSorterBroadcast.cs")]
    [InlineData("MainWindow.BroadcastWindows.cs")]
    public void ASkippedDescriptorImportNamesTheContract(string file)
    {
        var source = Assert.Single(AppSourceFile.LoadAll(file));

        Assert.Contains("\"FMS IMPORT SKIP\", $\"status={read.Status}\", \"contract=LIVE-BROADCAST\"", source.Text, StringComparison.Ordinal);
    }
}
