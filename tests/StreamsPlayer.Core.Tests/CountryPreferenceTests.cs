using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class CountryPreferenceTests
{
    [Fact]
    public void EveryShippedLanguageIsDeclaredAndOnlyEnglishLeadsWithNothing()
    {
        foreach (var entry in InterfaceLanguages.All)
        {
            var codes = CountryPreference.For(entry.Language);
            if (entry.Language == AppLanguage.English)
            {
                Assert.Empty(codes);
            }
            else
            {
                Assert.NotEmpty(codes);
            }
        }
    }

    // A leading country is offered with a flag and a name or not at all, so each code must be one the table knows.
    [Fact]
    public void EveryLeadingCodeIsAKnownCountry()
    {
        var known = CountryNames.Codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in InterfaceLanguages.All)
        {
            foreach (var code in CountryPreference.For(entry.Language))
            {
                Assert.True(known.Contains(code), $"{entry.DictionaryCode}: {code} is not in country-names.json.");
            }
        }
    }

    [Fact]
    public void UkrainianLeadsWithUkraineAlone() =>
        Assert.Equal("UA", Assert.Single(CountryPreference.For(AppLanguage.Ukrainian)));

    [Fact]
    public void FrenchLeadsWithTheFrancophoneCountries()
    {
        var french = CountryPreference.For(AppLanguage.French);
        Assert.Contains("FR", french);
        Assert.Contains("CA", french);
        Assert.Contains("SN", french);
        Assert.DoesNotContain("DE", french);
    }

    [Fact]
    public void AnUndefinedLanguageLeadsWithNothingInsteadOfThrowing() =>
        Assert.Empty(CountryPreference.For((AppLanguage)999));
}
