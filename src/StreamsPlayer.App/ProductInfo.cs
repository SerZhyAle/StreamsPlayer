using System.Reflection;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public enum ExchangePortalPage { Home, Account, AccountDeletion, Broadcasts }

public static class ProductInfo
{
    public const string Author = "Serhii Zhyhunenko / SerZhyAle";
    // SP-0040: the log-report recipient. Until now the address lived only in the README and the site copy,
    // so the application had no way to name the person it tells users to contact.
    public const string AuthorEmail = "sza@ukr.net";
    public const string AuthorUrl = "https://github.com/SerZhyAle";
    public const string SourceUrl = "https://github.com/SerZhyAle/StreamsPlayer";
    public const string WebsiteUrl = "https://serzhyale.github.io/StreamsPlayer/";
    public const string PrivacyUrl = "https://serzhyale.github.io/StreamsPlayer/privacy.html";
    public const string LicenceUrl = "https://github.com/SerZhyAle/StreamsPlayer/blob/main/LICENSE";

    // SP-0231 (SETTINGS-NAVIGATION 2.4, the About set): the support page is the site's own (contact address,
    // bug reporting, how to send logs); the releases page is the official download and update record.
    public const string SupportUrl = "https://serzhyale.github.io/StreamsPlayer/support.html";
    public const string ReleasesUrl = "https://github.com/SerZhyAle/StreamsPlayer/releases";
    // The related product of the set: the Android launcher whose site also hosts the media exchange portal
    // this product's exchange feature is documented through.
    public const string FastMediaSorterUrl = "https://serzhyale.github.io/FastMediaSorter_mob_v2/";

    public static string Version => BuildVersion.Of(Assembly.GetEntryAssembly());

    // The media exchange portal is one page set several products link to, hosted on the FastMediaSorter
    // Android site (shared pages proposal 2026-10-10). It has three editions - English, Russian, Ukrainian.
    private const string ExchangePortalBase =
        "https://serzhyale.github.io/FastMediaSorter_mob_v2/docs/media-exchange-portal/";

    // Same rule as InstructionsUrl: Russian and Ukrainian have their own edition, every other language
    // reads English.
    public static string ExchangePortalUrl(AppLanguage language, ExchangePortalPage page = ExchangePortalPage.Home)
    {
        var stem = page switch
        {
            ExchangePortalPage.Account => "exchange-server",
            // SP-0221 requirement 10: this product deletes no account; the portal page says where one is deleted.
            ExchangePortalPage.AccountDeletion => "account-deletion",
            ExchangePortalPage.Broadcasts => "live-broadcast",
            _ => "index"
        };
        var suffix = language switch
        {
            AppLanguage.Russian => "-ru",
            AppLanguage.Ukrainian => "-uk",
            _ => ""
        };
        return stem == "index" && suffix.Length == 0
            ? ExchangePortalBase : $"{ExchangePortalBase}{stem}{suffix}.html";
    }

    // SP-0029: each UI language points at its own README mirror; anything else falls back to English.
    public static string InstructionsUrl(AppLanguage language) => language switch
    {
        AppLanguage.Russian => $"{SourceUrl}/blob/main/README.ru.md",
        AppLanguage.Ukrainian => $"{SourceUrl}/blob/main/README.uk.md",
        _ => $"{SourceUrl}/blob/main/README.md"
    };
}
