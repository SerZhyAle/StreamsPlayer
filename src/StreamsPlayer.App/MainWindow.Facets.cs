using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// The four facet dropdowns (category, topic, language, country) and the one rule that governs how they
/// are refilled: a refill is a data refresh, not a user choice. Split out of <c>MainWindow.xaml.cs</c>,
/// which is well past the ~500 line budget.
/// </summary>
public partial class MainWindow
{
    private void PopulateFacets()
    {
        var hiddenIdentities = BuildHiddenIdentitySet();
        IEnumerable<StreamChannel> channels = _state.Channels;
        if (hiddenIdentities.Count > 0)
        {
            channels = channels.Where(channel => !IsHiddenBySet(hiddenIdentities, channel));
        }
        if (_state.HideAdultContent)
        {
            channels = channels.Where(channel => !CatalogTopics.IsAdult(channel.Topic));
        }

        IReadOnlyList<StreamChannel> universe = channels as IReadOnlyList<StreamChannel> ?? channels.ToList();
        var language = LocalizationService.CurrentLanguage;

        // Replacing a combo box's ItemsSource raises SelectionChanged synchronously, and FilterChanged
        // would answer each one with a debounced ApplyFilterNow - which scrolls to the catalog start and
        // zeroes the saved offset. Every edit, hide and refresh refills the facets, so each of them threw
        // the list to the top, and the startup scroll restore was undone by the same mechanism. The refill
        // is therefore guarded the way PopulateCollectionFilter guards its own; every caller already
        // follows with ApplyFilter, which is the evaluation the refill needs.
        var wasUpdating = _updatingLocalizedOptions;
        _updatingLocalizedOptions = true;
        var selectionChanged = false;
        try
        {
            selectionChanged |= SetFacet(CategoryFilter, universe.Select(channel => channel.Category));
            // SP-0061: built from the channels actually present, not from the registry, so a rubric with no
            // rows is not offered and a rubric this build has never heard of still is. Labels are translated;
            // the option's value stays the catalog's identifier.
            selectionChanged |= SetFacet(TopicFilter, universe.Select(channel => channel.Topic),
                label: TopicLabels.Text, order: TopicLabels.Comparer);
            // The catalog ships well over a hundred broadcast languages, so the one the user reads the
            // interface in leads the list (with its regional flavours) instead of being hunted for in an
            // alphabetical run. The selected value is deliberately untouched: this orders, it does not filter.
            selectionChanged |= SetFacet(LanguageFilter, universe.SelectMany(channel =>
                channel.Language?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? []),
                language);
            // The bank's `country` is an ISO 3166-1 alpha-2 code (STREAM-BANK). The option's value is that code,
            // whatever spelling the row carried ("Germany", "de", "UK"), so one country is one entry; what is
            // read is the full name in the interface language beside its flag. A value that resolves to no
            // code stays as written, without a flag - the contract's visible fallback.
            selectionChanged |= SetFacet(CountryFilter, universe.Select(channel => CatalogCountries.Normalize(channel.Country)),
                label: id => CountryNames.Label(id, language),
                icon: CountryFlags.For,
                labelOrder: StringComparer.Create(CultureInfo.CurrentUICulture, ignoreCase: true),
                // The countries of the interface language lead (all Francophone for French, Ukraine for Ukrainian);
                // English leads with none, so its list is one alphabet.
                featured: CountryPreference.For(language));
        }
        finally
        {
            _updatingLocalizedOptions = wasUpdating;
        }

        // A selection that fell back to "All" because its value left the catalog is a real change to the
        // filter, and the guard above swallowed the event that would have said so.
        if (selectionChanged && IsLoaded)
        {
            ScheduleFilterEvaluation();
        }
    }

    /// <summary>
    /// Fills a facet from the values present in the catalog. <paramref name="label"/> supplies a
    /// translated caption while the option keeps the catalog's own string as its value;
    /// <paramref name="order"/> replaces the default label ordering, and compares identifiers so a
    /// caller can order by something the alphabet alone does not express (SP-0061: General last).
    /// Returns whether the selected value is no longer the one the facet held before the refill.
    /// </summary>
    private static bool SetFacet(
        ComboBox comboBox,
        IEnumerable<string?> values,
        AppLanguage? preferred = null,
        Func<string, string>? label = null,
        IComparer<string>? order = null,
        Func<string, ImageSource?>? icon = null,
        IComparer<string>? labelOrder = null,
        IReadOnlySet<string>? featured = null)
    {
        var selected = SelectedOptionValue(comboBox) ?? AllValue;
        var options = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new UiOption(value!, label is null ? value! : label(value!), icon?.Invoke(value!)))
            .DistinctBy(value => value.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => preferred is { } language ? (int)CatalogLanguages.Match(value.Value, language) : 0);
        var ordered = (order is null
            ? options.ThenBy(value => value.Value == AllValue ? string.Empty : value.Label, labelOrder ?? StringComparer.OrdinalIgnoreCase)
            : options.ThenBy(value => value.Value, order)).ToList();

        // A lead block, when the caller names one: those options once, in the list's own order, ruled off
        // from the full list below - which still holds them, so the alphabet is complete and a reader who
        // goes straight to a letter finds every country there. The lead copies are distinct records, so the
        // combo box tells the two apart. Only options the catalog really has are lifted.
        var lead = featured is { Count: > 0 }
            ? ordered.Where(option => featured.Contains(option.Value)).ToList()
            : [];
        var items = new[] { new UiOption(AllValue, LocalizationService.Get("AllOption")) }
            .Concat(lead.Select((option, index) =>
                option with { Featured = true, EndsFeatured = index == lead.Count - 1 }))
            .Concat(ordered).ToList();
        var chosen = items.FirstOrDefault(item => item.Value.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        comboBox.ItemsSource = items;
        comboBox.SelectedItem = chosen;
        return !chosen.Value.Equals(selected, StringComparison.OrdinalIgnoreCase);
    }
}
