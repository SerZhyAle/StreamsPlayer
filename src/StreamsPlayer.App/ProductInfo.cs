using System.Reflection;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

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

    public static string Version =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "Unknown").Split('+')[0];

    // The media exchange portal is one page set several products link to, hosted on the FastMediaSorter
    // Android site (shared pages proposal 2026-10-10). It has three editions - English, Russian, Ukrainian.
    private const string ExchangePortalBase =
        "https://serzhyale.github.io/FastMediaSorter_mob_v2/docs/media-exchange-portal/";

    // Same rule as InstructionsUrl: Russian and Ukrainian have their own edition, every other language
    // reads English.
    public static string ExchangePortalUrl(AppLanguage language) => language switch
    {
        AppLanguage.Russian => $"{ExchangePortalBase}index-ru.html",
        AppLanguage.Ukrainian => $"{ExchangePortalBase}index-uk.html",
        _ => ExchangePortalBase
    };

    // SP-0029: each UI language points at its own README mirror; anything else falls back to English.
    public static string InstructionsUrl(AppLanguage language) => language switch
    {
        AppLanguage.Russian => $"{SourceUrl}/blob/main/README.ru.md",
        AppLanguage.Ukrainian => $"{SourceUrl}/blob/main/README.uk.md",
        _ => $"{SourceUrl}/blob/main/README.md"
    };
}
