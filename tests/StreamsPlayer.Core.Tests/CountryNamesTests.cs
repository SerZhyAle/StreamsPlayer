using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class CountryNamesTests
{
    [Fact]
    public void EveryCodeIsNamedInEveryShippedInterfaceLanguage()
    {
        foreach (var code in CountryNames.Codes)
        {
            foreach (var entry in InterfaceLanguages.All)
            {
                var name = CountryNames.Get(code, entry.Language);
                Assert.False(string.IsNullOrWhiteSpace(name), $"{code} has no {entry.DictionaryCode} name.");
            }
        }
    }

    [Theory]
    [InlineData("DE", AppLanguage.English, "Germany")]
    [InlineData("DE", AppLanguage.Russian, "Германия")]
    [InlineData("de", AppLanguage.Ukrainian, "Німеччина")]
    [InlineData("GB", AppLanguage.German, "Vereinigtes Königreich")]
    [InlineData("UA", AppLanguage.Chinese, "乌克兰")]
    public void NamesAreInTheInterfaceLanguageNotTheCountrysOwn(string code, AppLanguage language, string expected) =>
        Assert.Equal(expected, CountryNames.Get(code, language));

    [Fact]
    public void AnUnknownCodeHasNoNameAndTheLabelFallsBackToTheValueAsWritten()
    {
        Assert.Null(CountryNames.Get("ZZ", AppLanguage.English));
        Assert.Equal("Neverland", CountryNames.Label("Neverland", AppLanguage.Russian));
    }

    // A country is offered with a flag or not at all, and a flag with no name is dead weight. The flag
    // folder is the App's own, read here as data (no project reference).
    [Fact]
    public void TheFlagFolderAndTheNameTableCoverTheSameCountries()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "flags");
        var flags = Directory.GetFiles(folder, "*.png")
            .Select(path => Path.GetFileNameWithoutExtension(path).ToUpperInvariant())
            .Order(StringComparer.Ordinal)
            .ToList();
        var names = CountryNames.Codes.Order(StringComparer.Ordinal).ToList();

        Assert.NotEmpty(flags);
        Assert.Equal(names, flags);
    }
}
