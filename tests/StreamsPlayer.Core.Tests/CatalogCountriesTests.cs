using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class CatalogCountriesTests
{
    [Theory]
    [InlineData("CA", "CA")]
    [InlineData("de", "DE")]
    [InlineData("  gb  ", "GB")]
    public void ToCode_PassesTwoLetterCodesThrough(string value, string expected) =>
        Assert.Equal(expected, CatalogCountries.ToCode(value));

    [Theory]
    [InlineData("Germany", "DE")]
    [InlineData("USA", "US")]
    [InlineData("United Kingdom", "GB")]
    [InlineData("united kingdom", "GB")]
    [InlineData("Brasil", "BR")]
    [InlineData("The Russian Federation", "RU")]
    [InlineData("Россия", "RU")]
    public void ToCode_MapsTheSpelledOutNamesTheBankActuallyCarries(string value, string expected) =>
        Assert.Equal(expected, CatalogCountries.ToCode(value));

    // An unknown spelling shows no code rather than a guessed one: the column is an untrusted maintainer
    // claim, and a confidently wrong country is worse than a missing one.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Neverland")]
    [InlineData("XYZ")]
    [InlineData("D3")]
    public void ToCode_AnswersNullForWhatItCannotResolve(string? value) =>
        Assert.Null(CatalogCountries.ToCode(value));
}
