using System.Globalization;
using System.Windows;
using StreamPlayer.Core;

namespace StreamPlayer.App;

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
            Source = new Uri(language == AppLanguage.Russian
                ? "Localization.ru.xaml"
                : "Localization.en.xaml", UriKind.Relative)
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
        CultureInfo.CurrentUICulture = language == AppLanguage.Russian
            ? CultureInfo.GetCultureInfo("ru-RU")
            : CultureInfo.GetCultureInfo("en-US");
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string key) => Application.Current.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), arguments);
}

public sealed record UiOption(string Value, string Label)
{
    public override string ToString() => Label;
}
