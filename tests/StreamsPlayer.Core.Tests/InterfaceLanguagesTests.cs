using System.Globalization;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0034: guards the single declaration of the shipped language set. If any of these fail, some
/// surface has grown its own list.
/// </summary>
public sealed class InterfaceLanguagesTests
{
    [Fact]
    public void All_CoversEveryEnumMemberExactlyOnce()
    {
        var declared = Enum.GetValues<AppLanguage>();
        Assert.Equal(declared.Length, InterfaceLanguages.All.Count);
        Assert.Equal(
            declared.OrderBy(language => language).ToArray(),
            InterfaceLanguages.All.Select(entry => entry.Language).OrderBy(language => language).ToArray());
    }

    [Fact]
    public void All_ShipsThirteenLanguagesLedByEnglish()
    {
        Assert.Equal(13, InterfaceLanguages.All.Count);
        Assert.Equal(AppLanguage.English, InterfaceLanguages.All[0].Language);
        Assert.Equal(AppLanguage.English, InterfaceLanguages.Fallback);
    }

    [Theory]
    [InlineData("DictionaryCode")]
    [InlineData("CultureCode")]
    [InlineData("ListingCode")]
    public void All_CodesAreUniquePerSurface(string surface)
    {
        var codes = InterfaceLanguages.All.Select(entry => surface switch
        {
            "DictionaryCode" => entry.DictionaryCode,
            "CultureCode" => entry.CultureCode,
            _ => entry.ListingCode
        }).ToArray();

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(codes, string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void All_CultureCodesAreResolvableCultures()
    {
        foreach (var entry in InterfaceLanguages.All)
        {
            var culture = CultureInfo.GetCultureInfo(entry.CultureCode);
            Assert.Equal(entry.DictionaryCode, culture.TwoLetterISOLanguageName, ignoreCase: true);
        }
    }

    [Fact]
    public void All_MarksOnlyArabicAndUrduRightToLeft()
    {
        Assert.Equal(
            new[] { AppLanguage.Arabic, AppLanguage.Urdu },
            InterfaceLanguages.All.Where(entry => entry.RightToLeft).Select(entry => entry.Language).ToArray());
    }

    [Fact]
    public void For_ThrowsForAValueOutsideTheEnum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InterfaceLanguages.For((AppLanguage)99));
        Assert.False(InterfaceLanguages.IsShipped((AppLanguage)99));
        Assert.True(InterfaceLanguages.IsShipped(AppLanguage.Urdu));
    }

    [Theory]
    [InlineData("en", AppLanguage.English)]
    [InlineData("ru", AppLanguage.Russian)]
    [InlineData("uk", AppLanguage.Ukrainian)]
    [InlineData("de", AppLanguage.German)]
    [InlineData("it", AppLanguage.Italian)]
    [InlineData("es", AppLanguage.Spanish)]
    [InlineData("fr", AppLanguage.French)]
    [InlineData("pt", AppLanguage.Portuguese)]
    [InlineData("zh", AppLanguage.Chinese)]
    [InlineData("hi", AppLanguage.Hindi)]
    [InlineData("bn", AppLanguage.Bengali)]
    [InlineData("ar", AppLanguage.Arabic)]
    [InlineData("ur", AppLanguage.Urdu)]
    public void Match_ResolvesEveryShippedBaseCulture(string name, AppLanguage expected)
    {
        Assert.Equal(expected, InterfaceLanguages.Match(CultureInfo.GetCultureInfo(name)));
    }

    [Theory]
    [InlineData("de-AT", AppLanguage.German)]
    [InlineData("pt-PT", AppLanguage.Portuguese)]
    [InlineData("es-MX", AppLanguage.Spanish)]
    [InlineData("ar-EG", AppLanguage.Arabic)]
    [InlineData("zh-Hant-TW", AppLanguage.Chinese)]
    [InlineData("en-GB", AppLanguage.English)]
    public void Match_FoldsRegionalVariantsOntoTheShippedLanguage(string name, AppLanguage expected)
    {
        // We ship one variant per language, so pt-PT reads our Brazilian Portuguese and zh-Hant our
        // Simplified Chinese. That is closer than dropping the user to English.
        Assert.Equal(expected, InterfaceLanguages.Match(CultureInfo.GetCultureInfo(name)));
    }

    [Theory]
    [InlineData("pl-PL")]
    [InlineData("ja-JP")]
    [InlineData("sv-SE")]
    [InlineData("id-ID")]
    public void Match_ReturnsNullForAnUnshippedCulture(string name)
    {
        Assert.Null(InterfaceLanguages.Match(CultureInfo.GetCultureInfo(name)));
    }

    [Fact]
    public void Match_ReturnsNullForNullAndInvariant()
    {
        Assert.Null(InterfaceLanguages.Match(null));
        Assert.Null(InterfaceLanguages.Match(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Detect_PrefersTheDisplayCultureOverTheInstalledOne()
    {
        Assert.Equal(
            AppLanguage.Hindi,
            InterfaceLanguages.Detect(CultureInfo.GetCultureInfo("hi-IN"), CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Detect_FallsThroughToTheInstalledCultureThenToEnglish()
    {
        Assert.Equal(
            AppLanguage.Bengali,
            InterfaceLanguages.Detect(CultureInfo.GetCultureInfo("ja-JP"), CultureInfo.GetCultureInfo("bn-BD")));
        Assert.Equal(
            AppLanguage.English,
            InterfaceLanguages.Detect(CultureInfo.GetCultureInfo("ja-JP"), CultureInfo.GetCultureInfo("pl-PL")));
        Assert.Equal(AppLanguage.English, InterfaceLanguages.Detect(null, null));
    }

    [Fact]
    public void Detect_AlwaysReturnsAShippedLanguage()
    {
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            Assert.True(InterfaceLanguages.IsShipped(InterfaceLanguages.Detect(culture, null)));
        }
    }
}
