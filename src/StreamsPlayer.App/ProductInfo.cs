using System.Reflection;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public static class ProductInfo
{
    public const string Author = "Serhii Zhyhunenko / SerZhyAle";
    public const string AuthorUrl = "https://github.com/SerZhyAle";
    public const string SourceUrl = "https://github.com/SerZhyAle/StreamsPlayer";
    public const string WebsiteUrl = "https://serzhyale.github.io/StreamsPlayer/";
    public const string PrivacyUrl = "https://serzhyale.github.io/StreamsPlayer/privacy.html";

    public static string Version =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "Unknown").Split('+')[0];

    // SP-0029: each UI language points at its own README mirror; anything else falls back to English.
    public static string InstructionsUrl(AppLanguage language) => language switch
    {
        AppLanguage.Russian => $"{SourceUrl}/blob/main/README.ru.md",
        AppLanguage.Ukrainian => $"{SourceUrl}/blob/main/README.uk.md",
        _ => $"{SourceUrl}/blob/main/README.md"
    };
}
