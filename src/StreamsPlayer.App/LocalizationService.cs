using System.Globalization;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public static class LocalizationService
{
    private const string DictionaryPrefix = "Localization.";

    public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.English;

    /// <summary>
    /// The chosen language's dictionary, merged above the source one; null while the source language
    /// itself is chosen.
    /// </summary>
    private static ResourceDictionary? _overlay;

    /// <remarks>
    /// <c>APP-BEHAVIOUR</c> rule 7 (0.11 ladder): a missing translation renders the source-language string,
    /// then the key. The source dictionary - English, the one every key is authored in and the parity
    /// reference - therefore stays merged for the life of the process, and the chosen language is laid over
    /// it, so a key the overlay lacks resolves to English in XAML and in <see cref="Get"/> alike. Replacing
    /// the one dictionary, as this did before SP-0247, left a key missing from the chosen language with
    /// nothing beneath it.
    /// </remarks>
    public static void Apply(AppLanguage language)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var source = SourceDictionary(dictionaries);
        if (_overlay is not null)
        {
            dictionaries.Remove(_overlay);
            _overlay = null;
        }

        var entry = InterfaceLanguages.For(language);
        if (entry.Language != InterfaceLanguages.Fallback)
        {
            _overlay = Dictionary(entry.DictionaryCode);
            // A later merged dictionary wins a lookup, so the overlay sits directly above the source.
            dictionaries.Insert(dictionaries.IndexOf(source) + 1, _overlay);
        }

        CurrentLanguage = language;

        // SP-0034: worker threads must format text in the selected language too, so set the thread
        // default alongside the current thread's. CurrentCulture is deliberately left alone - catalog
        // parsing and the persisted state depend on its existing behaviour.
        var culture = CreateUiCulture(entry.CultureCode);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private static ResourceDictionary SourceDictionary(IList<ResourceDictionary> dictionaries)
    {
        var sourceCode = InterfaceLanguages.For(InterfaceLanguages.Fallback).DictionaryCode;
        var source = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.EndsWith(FileName(sourceCode), StringComparison.OrdinalIgnoreCase) == true);
        if (source is not null)
        {
            return source;
        }

        source = Dictionary(sourceCode);
        dictionaries.Insert(0, source);
        return source;
    }

    private static string FileName(string dictionaryCode) => $"{DictionaryPrefix}{dictionaryCode}.xaml";

    private static ResourceDictionary Dictionary(string dictionaryCode) =>
        new() { Source = new Uri(FileName(dictionaryCode), UriKind.Relative) };

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
    /// escapes. Composes with <see cref="Get"/>'s own degradation: a key the chosen language lacks renders in
    /// English, and a key no dictionary holds renders as the key name.
    /// </summary>
    public static string Format(string key, params object?[] arguments) =>
        LocalizedFormat.Apply(CultureInfo.CurrentUICulture, Get(key), arguments);
}

/// <param name="Value">What the option stands for in data (a catalog identifier), never translated.</param>
/// <param name="Label">What the person reads.</param>
/// <param name="Icon">An optional picture drawn beside the label, for a facet that has one (the country flag).</param>
/// <param name="Featured">Whether this is the lead copy of an option a facet lifts above its alphabetical list.
/// The copy inside the list is a different record, so the two never compare equal and a combo box can tell them apart.</param>
/// <param name="EndsFeatured">Whether the lead block ends here, so the list can rule a line under it.</param>
public sealed record UiOption(
    string Value,
    string Label,
    System.Windows.Media.ImageSource? Icon = null,
    bool Featured = false,
    bool EndsFeatured = false)
{
    public override string ToString() => Label;
}
