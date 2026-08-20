namespace StreamsPlayer.Core;

/// <summary>
/// SP-0087: the stream bank's <c>country</c> column reduced to a two-letter code, or to nothing.
/// </summary>
/// <remarks>
/// The column is not normalized upstream. On 2026-08-19 the live bank held 198 distinct values over
/// 19 534 rows: 143 of them already a two-letter code, and 54 spelled out - <c>Germany</c> 137 rows,
/// <c>USA</c> 101, <c>United Kingdom</c> 57, <c>Belgium</c> 46, down a long tail to <c>Россия</c> 1.
/// <para>
/// An unrecognized spelling answers <c>null</c> and the interface shows no code. Guessing is the one
/// thing this class must not do: a wrong country stated confidently is worse than none, and the value
/// is an untrusted maintainer claim to begin with.
/// </para>
/// </remarks>
public static class CatalogCountries
{
    private static readonly IReadOnlyDictionary<string, string> SpelledOut =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Germany"] = "DE",
            ["USA"] = "US",
            ["United Kingdom"] = "GB",
            ["Belgium"] = "BE",
            ["Brazil"] = "BR",
            ["Brasil"] = "BR",
            ["Netherlands"] = "NL",
            ["France"] = "FR",
            ["Switzerland"] = "CH",
            ["Poland"] = "PL",
            ["Lithuania"] = "LT",
            ["Australia"] = "AU",
            ["India"] = "IN",
            ["Spain"] = "ES",
            ["Italy"] = "IT",
            ["Denmark"] = "DK",
            ["Russia"] = "RU",
            ["The Russian Federation"] = "RU",
            ["Россия"] = "RU",
            ["Portugal"] = "PT",
            ["China"] = "CN",
            ["Israel"] = "IL",
            ["Indonesia"] = "ID",
            ["Canada"] = "CA",
            ["Slovakia"] = "SK",
            ["Sweden"] = "SE",
            ["Costa Rica"] = "CR",
            ["Greece"] = "GR",
            ["Austria"] = "AT",
            ["Mexico"] = "MX",
            ["Argentina"] = "AR",
            ["Romania"] = "RO",
            ["New Zealand"] = "NZ",
            ["Finland"] = "FI",
            ["Luxembourg"] = "LU",
            ["Hungary"] = "HU",
            ["Malaysia"] = "MY",
            ["Cuba"] = "CU",
            ["Syria"] = "SY",
            ["Bulgaria"] = "BG",
            ["Czech Republic"] = "CZ",
            ["Equatorial Guinea"] = "GQ",
            ["Ukraine"] = "UA",
            ["Bermuda"] = "BM",
            ["Bahamas"] = "BS",
            ["Moldova"] = "MD",
            ["Nigeria"] = "NG",
            ["Macedonia"] = "MK",
            ["Estonia"] = "EE",
            ["Kyrgyzstan"] = "KG",
            ["Japan"] = "JP",
            ["Serbia"] = "RS",
            ["Ghana"] = "GH",
            // The bank names a constituent country. ISO 3166-1 has no code for it, and GB is the only
            // honest two-letter answer; inventing one would be exactly what this class exists to avoid.
            ["Wales"] = "GB"
        };

    /// <summary>The two-letter code for a bank country value, or <c>null</c> when there is no honest one.</summary>
    public static string? ToCode(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && char.IsAsciiLetter(trimmed[1]))
        {
            return trimmed.ToUpperInvariant();
        }

        return SpelledOut.TryGetValue(trimmed, out var code) ? code : null;
    }
}
