using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// Guards the relation between the shipped interface languages and the broadcast-language tokens the
/// stream bank publishes. The catalog facet is ordered by this, so a wrong answer silently buries the
/// user's own language in the alphabetical run.
/// </summary>
public sealed class CatalogLanguagesTests
{
    [Theory]
    [InlineData("russian", AppLanguage.Russian)]
    [InlineData("RUSSIAN", AppLanguage.Russian)]
    [InlineData("  portuguese  ", AppLanguage.Portuguese)]
    [InlineData("english", AppLanguage.English)]
    public void Match_NamesTheLanguageExactly(string token, AppLanguage language) =>
        Assert.Equal(CatalogLanguageMatch.Exact, CatalogLanguages.Match(token, language));

    [Theory]
    [InlineData("brazilian portuguese", AppLanguage.Portuguese)]
    [InlineData("american english", AppLanguage.English)]
    [InlineData("british english", AppLanguage.English)]
    [InlineData("mandarin chinese", AppLanguage.Chinese)]
    public void Match_RecognizesARegionalFlavour(string token, AppLanguage language) =>
        Assert.Equal(CatalogLanguageMatch.Variant, CatalogLanguages.Match(token, language));

    [Theory]
    [InlineData("german", AppLanguage.English)]
    [InlineData("cantonese", AppLanguage.Chinese)]
    [InlineData("englishman", AppLanguage.English)]
    [InlineData("", AppLanguage.English)]
    [InlineData(null, AppLanguage.English)]
    public void Match_LeavesAnUnrelatedTokenAlone(string? token, AppLanguage language) =>
        Assert.Equal(CatalogLanguageMatch.None, CatalogLanguages.Match(token, language));

    /// <summary>
    /// The facet's actual sort key: exact first, then flavours, then everything else alphabetically.
    /// </summary>
    [Fact]
    public void Match_OrdersTheFacetWithTheInterfaceLanguageFirst()
    {
        string[] facet = ["akan", "brazilian portuguese", "english", "german", "portuguese"];
        var ordered = facet
            .OrderBy(token => (int)CatalogLanguages.Match(token, AppLanguage.Portuguese))
            .ThenBy(token => token, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(["portuguese", "brazilian portuguese", "akan", "english", "german"], ordered);
    }

    /// <summary>
    /// The whole design rests on the enum member names matching the catalog's English language names.
    /// A renamed member (or a new language whose catalog token differs) must fail here, not in the UI.
    /// </summary>
    [Fact]
    public void Match_EveryShippedLanguageMatchesItsLowercaseCatalogToken()
    {
        foreach (var entry in InterfaceLanguages.All)
        {
            var token = entry.Language.ToString().ToLowerInvariant();
            Assert.Equal(CatalogLanguageMatch.Exact, CatalogLanguages.Match(token, entry.Language));
        }
    }
}
