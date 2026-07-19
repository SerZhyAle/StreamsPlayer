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

    public static string InstructionsUrl(AppLanguage language) => language == AppLanguage.Russian
        ? $"{SourceUrl}/blob/main/README.ru.md"
        : $"{SourceUrl}/blob/main/README.md";
}
