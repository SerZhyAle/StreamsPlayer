using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>
/// One shipped interface language and the code every surface knows it by.
/// </summary>
/// <param name="Language">Identity, and the token persisted in <see cref="CatalogState.Language"/>.</param>
/// <param name="DictionaryCode">Suffix of the app's <c>Localization.&lt;code&gt;.xaml</c> and of the site's per-language folder.</param>
/// <param name="CultureCode">The .NET culture applied to formatting when this language is selected.</param>
/// <param name="ListingCode">The Partner Center column name, which is not always the dictionary code.</param>
/// <param name="RightToLeft">Whether the interface mirrors for this language.</param>
public sealed record InterfaceLanguage(
    AppLanguage Language,
    string DictionaryCode,
    string CultureCode,
    string ListingCode,
    bool RightToLeft);

/// <summary>
/// SP-0034: the one declaration of the shipped language set. Every surface that names a language -
/// dictionaries, the picker, the site generator, the Store listing builder, the screenshot pipeline -
/// derives from or is checked against this list. Before SP-0034 the set was asserted independently in
/// six places and had already drifted from reality; do not add a seventh.
/// </summary>
public static class InterfaceLanguages
{
    /// <summary>
    /// Every shipped language, in the order the picker presents them. Portuguese and Chinese use
    /// two-letter app codes while shipping Brazilian Portuguese and Simplified Chinese - only the
    /// listing code spells the variant out, matching the sibling product's Partner Center columns.
    /// </summary>
    public static IReadOnlyList<InterfaceLanguage> All { get; } =
    [
        new(AppLanguage.English, "en", "en-US", "en-us", RightToLeft: false),
        new(AppLanguage.Russian, "ru", "ru-RU", "ru", RightToLeft: false),
        new(AppLanguage.Ukrainian, "uk", "uk-UA", "uk", RightToLeft: false),
        new(AppLanguage.German, "de", "de-DE", "de", RightToLeft: false),
        new(AppLanguage.Italian, "it", "it-IT", "it", RightToLeft: false),
        new(AppLanguage.Spanish, "es", "es-ES", "es", RightToLeft: false),
        new(AppLanguage.French, "fr", "fr-FR", "fr", RightToLeft: false),
        new(AppLanguage.Portuguese, "pt", "pt-BR", "pt-br", RightToLeft: false),
        new(AppLanguage.Chinese, "zh", "zh-Hans", "zh-hans", RightToLeft: false),
        new(AppLanguage.Hindi, "hi", "hi-IN", "hi", RightToLeft: false),
        new(AppLanguage.Bengali, "bn", "bn-BD", "bn", RightToLeft: false),
        new(AppLanguage.Arabic, "ar", "ar-SA", "ar", RightToLeft: true),
        new(AppLanguage.Urdu, "ur", "ur-PK", "ur", RightToLeft: true)
    ];

    /// <summary>The default when nothing else resolves. Never Russian, whatever the author's own locale is.</summary>
    public const AppLanguage Fallback = AppLanguage.English;

    private static readonly Dictionary<AppLanguage, InterfaceLanguage> ByLanguage =
        All.ToDictionary(entry => entry.Language);

    private static readonly Dictionary<string, AppLanguage> ByDictionaryCode =
        All.ToDictionary(entry => entry.DictionaryCode, entry => entry.Language, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The registry entry for a language. Throws for a value outside the enum, because a caller holding
    /// an undefined <see cref="AppLanguage"/> has a bug that must not be papered over with English -
    /// state read from disk is normalized on load instead.
    /// </summary>
    public static InterfaceLanguage For(AppLanguage language) =>
        ByLanguage.TryGetValue(language, out var entry)
            ? entry
            : throw new ArgumentOutOfRangeException(nameof(language), language, "Not a shipped interface language.");

    /// <summary>Whether the value is a defined, shipped language rather than an arbitrary cast integer.</summary>
    public static bool IsShipped(AppLanguage language) => ByLanguage.ContainsKey(language);

    /// <summary>
    /// The language a culture asks for, or <c>null</c> when we do not ship it. Matching is on the base
    /// subtag, so every regional variant resolves to the language we translated: de-AT to German,
    /// pt-PT to our Brazilian Portuguese, zh-Hant to our Simplified Chinese. That is a deliberate
    /// approximation - shipping one variant per language is the ticket's scope - and it is better than
    /// dropping such a user to English.
    /// </summary>
    public static AppLanguage? Match(CultureInfo? culture)
    {
        if (culture is null || culture.Equals(CultureInfo.InvariantCulture))
        {
            return null;
        }

        return ByDictionaryCode.TryGetValue(culture.TwoLetterISOLanguageName, out var language) ? language : null;
    }

    /// <summary>
    /// The language a fresh install starts in. The user's display language wins; the language Windows
    /// itself was installed in is only a second guess; English is the floor. Cultures are arguments
    /// rather than ambient state so the whole matrix is testable off the current machine.
    /// </summary>
    public static AppLanguage Detect(CultureInfo? display, CultureInfo? installed) =>
        Match(display) ?? Match(installed) ?? Fallback;
}
