namespace StreamsPlayer.Core;

/// <summary>
/// How closely a catalog language token relates to a shipped interface language. Members are ordered
/// closest first, so the value doubles as a sort key for the language facet.
/// </summary>
public enum CatalogLanguageMatch
{
    /// <summary>The token names exactly the interface language ("russian" for Russian).</summary>
    Exact,

    /// <summary>The token names a regional flavour of it ("brazilian portuguese" for Portuguese).</summary>
    Variant,

    /// <summary>An unrelated language.</summary>
    None
}

/// <summary>
/// The stream bank spells a channel's language as a lowercase English name - "english", "portuguese" -
/// and a regional flavour as a multi-word token - "brazilian portuguese", "american english". Those
/// names coincide with the <see cref="AppLanguage"/> member names, which is what lets the interface
/// language be related to the catalog facet without maintaining a second lookup table beside
/// <see cref="InterfaceLanguages"/>.
/// </summary>
public static class CatalogLanguages
{
    private static readonly char[] WordSeparators = [' ', ',', '-', '_', '/', '(', ')'];

    /// <summary>
    /// Relates one catalog language token to an interface language. Matching is per word rather than by
    /// substring so a flavour is recognized ("american english") while an unrelated language that merely
    /// contains the name is not.
    /// </summary>
    public static CatalogLanguageMatch Match(string? catalogLanguage, AppLanguage language)
    {
        if (string.IsNullOrWhiteSpace(catalogLanguage))
        {
            return CatalogLanguageMatch.None;
        }

        var name = language.ToString();
        var token = catalogLanguage.Trim();
        if (token.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return CatalogLanguageMatch.Exact;
        }

        return token
            .Split(WordSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(word => word.Equals(name, StringComparison.OrdinalIgnoreCase))
                ? CatalogLanguageMatch.Variant
                : CatalogLanguageMatch.None;
    }
}
