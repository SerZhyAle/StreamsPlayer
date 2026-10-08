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

    // `STREAM-BANK`: the `uk` alias folds to the code. Before this the facet listed UK and GB as two countries.
    [Theory]
    [InlineData("UK", "GB")]
    [InlineData("uk", "GB")]
    [InlineData(" Uk ", "GB")]
    public void ToCode_FoldsTheUkAliasIntoGb(string value, string expected) =>
        Assert.Equal(expected, CatalogCountries.ToCode(value));

    // XX is the bank's "unknown" placeholder (one row in the bundled snapshot), not a country.
    [Theory]
    [InlineData("XX")]
    [InlineData("xx")]
    public void ToCode_TreatsThePlaceholderAsNoCountry(string value) =>
        Assert.Null(CatalogCountries.ToCode(value));

    [Theory]
    [InlineData("DE", "DE")]
    [InlineData("de", "DE")]
    [InlineData("Germany", "DE")]
    [InlineData("UK", "GB")]
    [InlineData("Wales", "GB")]
    public void Normalize_GivesOneIdentityPerCountryWhateverTheSpelling(string value, string expected) =>
        Assert.Equal(expected, CatalogCountries.Normalize(value));

    // The contract's visible fallback: what cannot be resolved stays as written, trimmed, never invented;
    // blank and the placeholder stay out of the facet altogether.
    [Fact]
    public void Normalize_KeepsAnUnrecognizedValueVerbatim() =>
        Assert.Equal("Neverland", CatalogCountries.Normalize("  Neverland "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("XX")]
    public void Normalize_AnswersNullForBlankAndThePlaceholder(string? value) =>
        Assert.Null(CatalogCountries.Normalize(value));

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
