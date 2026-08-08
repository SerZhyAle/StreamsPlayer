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
        var culture = CreateUiCulture(entry.CultureCode);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    /// <summary>
    /// The interface culture, with dates pinned to the Gregorian calendar.
    /// </summary>
    /// <remarks>
    /// SP-0034, found in the Arabic capture: <c>ar-SA</c> defaults to the Umm al-Qura calendar, so the
    /// catalog timestamp read <c>1448/02/12 بعد الهجرة</c> while Windows, the file system and every
    /// other application showed 2026-07-26. Choosing an interface language must change the words, not
    /// the calendar the user has to reconcile with the rest of their desktop. Month and day names,
    /// digits and ordering still follow the chosen language.
    /// </remarks>
    private static CultureInfo CreateUiCulture(string cultureCode)
    {
        var culture = CultureInfo.GetCultureInfo(cultureCode);
        if (culture.DateTimeFormat.Calendar is GregorianCalendar)
        {
            return culture;
        }

        var gregorian = culture.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault();
        if (gregorian is null)
        {
            return culture;
        }

        var adjusted = (CultureInfo)culture.Clone();
        adjusted.DateTimeFormat.Calendar = gregorian;
        return adjusted;
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

    /// <summary>
    /// SP-0057: <see cref="LocalizedFormat"/> rather than <c>string.Format</c>, because every caller is an
    /// <c>async void</c> event handler and none of them filters <see cref="FormatException"/> - a string
    /// that gained a placeholder its call site does not supply used to end the process. The gate in
    /// <c>LocalizedCallSiteTests</c> is what stops that being written; this is what it costs if one
    /// escapes. Composes with <see cref="Get"/>'s own degradation: an unknown key renders as the key name.
    /// </summary>
    public static string Format(string key, params object?[] arguments) =>
        LocalizedFormat.Apply(CultureInfo.CurrentUICulture, Get(key), arguments);
}

public sealed record UiOption(string Value, string Label)
{
    public override string ToString() => Label;
}
