using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

public sealed class ExchangePortalTests
{
    [Theory]
    [InlineData(AppLanguage.English, ExchangePortalPage.Home, "")]
    [InlineData(AppLanguage.Russian, ExchangePortalPage.Home, "index-ru.html")]
    [InlineData(AppLanguage.Ukrainian, ExchangePortalPage.Home, "index-uk.html")]
    [InlineData(AppLanguage.Russian, ExchangePortalPage.Account, "exchange-server-ru.html")]
    [InlineData(AppLanguage.Ukrainian, ExchangePortalPage.Broadcasts, "live-broadcast-uk.html")]
    public void ChoosesThePublishedLocaleRoute(AppLanguage language, ExchangePortalPage page, string route)
    {
        Assert.Equal("https://serzhyale.github.io/FastMediaSorter_mob_v2/docs/media-exchange-portal/" + route,
            ProductInfo.ExchangePortalUrl(language, page));
    }

    [Fact]
    public void EveryOtherInterfaceLanguageUsesEnglishForEveryTopic()
    {
        foreach (var language in Enum.GetValues<AppLanguage>().Except([AppLanguage.Russian, AppLanguage.Ukrainian]))
        {
            foreach (var page in Enum.GetValues<ExchangePortalPage>())
            {
                Assert.Equal(ProductInfo.ExchangePortalUrl(AppLanguage.English, page), ProductInfo.ExchangePortalUrl(language, page));
            }
        }
    }
}
