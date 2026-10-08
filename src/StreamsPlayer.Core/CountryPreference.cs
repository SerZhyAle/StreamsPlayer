namespace StreamsPlayer.Core;

/// <summary>
/// The countries a reader of an interface language most plausibly looks for first: where that language is
/// an official one. The country facet lifts them above the alphabetical list (and repeats them inside it).
/// </summary>
/// <remarks>
/// English has none on purpose: it is official nearly everywhere, so a lead block would only be a second,
/// arbitrary alphabet. A code the catalog does not carry is simply not offered, so a list may name more
/// countries than any one bank holds. Codes are ISO 3166-1 alpha-2, as in <see cref="CatalogCountries"/>;
/// <c>CountryPreferenceTests</c> checks each one against <see cref="CountryNames"/>.
/// </remarks>
public static class CountryPreference
{
    private static readonly IReadOnlyDictionary<AppLanguage, IReadOnlySet<string>> Leading =
        new Dictionary<AppLanguage, IReadOnlySet<string>>
        {
            [AppLanguage.English] = Set(),
            [AppLanguage.Russian] = Set("RU", "BY", "KZ", "KG"),
            [AppLanguage.Ukrainian] = Set("UA"),
            [AppLanguage.German] = Set("DE", "AT", "CH", "LI", "LU", "BE"),
            [AppLanguage.Italian] = Set("IT", "SM", "VA", "CH"),
            [AppLanguage.Spanish] = Set(
                "ES", "MX", "AR", "CO", "PE", "VE", "CL", "EC", "GT", "CU", "BO", "DO", "HN", "PY", "SV",
                "NI", "CR", "PA", "UY", "PR", "GQ"),
            // Francophone: France and its overseas departments and collectivities, the European and North
            // American states with French as an official language, and the African and island states where
            // it is official.
            [AppLanguage.French] = Set(
                "FR", "BE", "CH", "LU", "MC", "CA", "HT",
                "GF", "GP", "MQ", "RE", "YT", "PM", "BL", "MF", "NC", "PF", "WF",
                "BJ", "BF", "BI", "CM", "CF", "TD", "KM", "CG", "CD", "CI", "DJ", "GA", "GN", "GQ", "MG",
                "ML", "NE", "RW", "SN", "SC", "TG", "VU"),
            [AppLanguage.Portuguese] = Set("PT", "BR", "AO", "MZ", "CV", "GW", "ST", "TL", "MO", "GQ"),
            [AppLanguage.Chinese] = Set("CN", "TW", "HK", "MO", "SG"),
            [AppLanguage.Hindi] = Set("IN"),
            [AppLanguage.Bengali] = Set("BD", "IN"),
            [AppLanguage.Arabic] = Set(
                "SA", "AE", "BH", "DZ", "KM", "DJ", "EG", "IQ", "JO", "KW", "LB", "LY", "MR", "MA", "OM",
                "PS", "QA", "SO", "SD", "SY", "TN", "YE"),
            [AppLanguage.Urdu] = Set("PK", "IN")
        };

    /// <summary>The codes to lead the country list with for <paramref name="language"/>; empty for none.</summary>
    public static IReadOnlySet<string> For(AppLanguage language) =>
        Leading.TryGetValue(language, out var codes) ? codes : EmptySet;

    private static readonly IReadOnlySet<string> EmptySet = Set();

    private static HashSet<string> Set(params string[] codes) => new(codes, StringComparer.OrdinalIgnoreCase);
}
