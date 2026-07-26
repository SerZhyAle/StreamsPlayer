using System.Globalization;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public static class LocalizationService
{
    private const string DictionaryPrefix = "Localization.";

    public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.English;

    public static void Apply(AppLanguage language)
    {
        var application = Application.Current;
        var dictionaries = application.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains(DictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true);
        var entry = InterfaceLanguages.For(language);
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"{DictionaryPrefix}{entry.DictionaryCode}.xaml", UriKind.Relative)
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

        // SP-0034: worker threads must format text in the selected language too, so set the thread
        // default alongside the current thread's. CurrentCulture is deliberately left alone - catalog
        // parsing and the persisted state depend on its existing behaviour.
        var culture = CultureInfo.GetCultureInfo(entry.CultureCode);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    /// <summary>
    /// Each language named in its own language. Every dictionary carries identical values for these
    /// keys on purpose: a picker that renamed a language when you switched locale would be unusable
    /// for the person trying to get back.
    /// </summary>
    public static string NativeName(AppLanguage language) =>
        Get(EndonymKey(InterfaceLanguages.For(language).Language));

    /// <summary>The dictionary key holding a language's endonym, derived from the enum member name.</summary>
    public static string EndonymKey(AppLanguage language) => $"Language{language}";

    public static string Get(string key) => Application.Current.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), arguments);
}

public sealed record UiOption(string Value, string Label)
{
    public override string ToString() => Label;
}
