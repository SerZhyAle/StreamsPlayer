using System.Globalization;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public static class LocalizationService
{
    private const string DictionaryPrefix = "Localization.";

    public static event EventHandler? LanguageChanged;
    public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.English;

    public static void Apply(AppLanguage language)
    {
        var application = Application.Current;
        var dictionaries = application.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains(DictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"Localization.{DictionaryCode(language)}.xaml", UriKind.Relative)
        };
        if (current is null)
        {
            dictionaries.Insert(0, replacement);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(current)] = replacement;
        }

        CurrentLanguage = language;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(CultureCode(language));
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    // SP-0029: ISO codes only; any "UA"-style text belongs to display labels, never to code or state.
    private static string DictionaryCode(AppLanguage language) => language switch
    {
        AppLanguage.Russian => "ru",
        AppLanguage.Ukrainian => "uk",
        _ => "en"
    };

    private static string CultureCode(AppLanguage language) => language switch
    {
        AppLanguage.Russian => "ru-RU",
        AppLanguage.Ukrainian => "uk-UA",
        _ => "en-US"
    };

    /// <summary>The languages the picker offers, in display order.</summary>
    public static IReadOnlyList<AppLanguage> Available { get; } =
        [AppLanguage.English, AppLanguage.Russian, AppLanguage.Ukrainian];

    /// <summary>
    /// Each language named in its own language. The three dictionaries carry identical values for
    /// these keys on purpose: a picker that renamed a language when you switched locale would be
    /// unusable for the person trying to get back.
    /// </summary>
    public static string NativeName(AppLanguage language) => Get(language switch
    {
        AppLanguage.Russian => "LanguageRussian",
        AppLanguage.Ukrainian => "LanguageUkrainian",
        _ => "LanguageEnglish"
    });

    /// <summary>Two-letter badge shown on the collapsed picker button.</summary>
    public static string ShortCode(AppLanguage language) => language switch
    {
        AppLanguage.Russian => "RU",
        AppLanguage.Ukrainian => "UK",
        _ => "EN"
    };

    public static string Get(string key) => Application.Current.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), arguments);
}

public sealed record UiOption(string Value, string Label)
{
    public override string ToString() => Label;
}
