namespace StreamsPlayer.Core;

/// <summary>
/// The stream bank's closed rubric vocabulary (SP-0061).
/// </summary>
/// <remarks>
/// <para>
/// The <c>topic</c> column used to be free text - 437 distinct values over 3916 rows, 333 of them used
/// once or twice. The publisher now folds every incoming value into the set below while it builds the
/// catalog, so each row arrives canonical and the value finally becomes translatable.
/// </para>
/// <para>
/// Two rules this class exists to keep. First, <b>the identifier is data</b>: it is stored, merged and
/// exported exactly as the bank spells it, and this class only says which localization key names it.
/// Resolving that key to a word is the interface's job, which is also what keeps Core free of WPF.
/// Second, <b>an unknown identifier is not an error</b>: a catalog newer than the app, and a manually
/// added channel with a hand-typed rubric, both produce values outside the set. <see cref="ResourceKey"/>
/// answers <c>null</c> for those, and every caller is expected to fall back to showing the identifier
/// rather than hiding the channel or rewriting it to <see cref="General"/>.
/// </para>
/// <para>
/// A rubric is not a <c>category</c>. That is a different column over a different, disjoint vocabulary
/// (Radio, Live TV, Radio (SomaFM), Test stream, Open movies) and the two never share a control.
/// </para>
/// </remarks>
public static class CatalogTopics
{
    /// <summary>
    /// The bank's residual rubric: 9562 of 19855 rows on 2026-08-07, 48.2% of the catalog. Named here
    /// because a facet that offers it in alphabetical order lets a pick which narrows almost nothing
    /// sit among rubrics that narrow a great deal - the interface orders it last instead.
    /// </summary>
    public const string General = "General";

    /// <summary>
    /// Identifier to localization key. Ordered as the vocabulary was published, not alphabetically:
    /// the display order is a presentation decision and belongs to whatever renders the list.
    /// <para>
    /// <c>Test</c> is declared by the publisher and currently matches zero rows - the 25 rows of the
    /// <c>Test stream</c> *category* carry <see cref="General"/>. It is kept so a future row arrives
    /// with a label instead of a raw identifier.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Keys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [General] = "TopicGeneral",
            ["News"] = "TopicNews",
            ["Talk"] = "TopicTalk",
            ["Sports"] = "TopicSports",
            ["Business"] = "TopicBusiness",
            ["Education"] = "TopicEducation",
            ["Documentary"] = "TopicDocumentary",
            ["Comedy"] = "TopicComedy",
            ["Lifestyle"] = "TopicLifestyle",
            ["Shopping"] = "TopicShopping",
            ["Religious"] = "TopicReligious",
            ["Kids"] = "TopicKids",
            ["Adult"] = "TopicAdult",
            ["Movies & Series"] = "TopicMoviesSeries",
            ["Local radio"] = "TopicLocalRadio",
            ["Traffic cams"] = "TopicTrafficCams",
            ["Webcam"] = "TopicWebcam",
            ["Test"] = "TopicTest",
            ["Pop"] = "TopicPop",
            ["Rock"] = "TopicRock",
            ["Metal"] = "TopicMetal",
            ["Electronic"] = "TopicElectronic",
            ["Chillout"] = "TopicChillout",
            ["Hip-hop"] = "TopicHipHop",
            ["R&B & Soul"] = "TopicRnbSoul",
            ["Jazz & Blues"] = "TopicJazzBlues",
            ["Classical"] = "TopicClassical",
            ["Country & Folk"] = "TopicCountryFolk",
            ["Reggae"] = "TopicReggae",
            ["Latin"] = "TopicLatin",
            ["World"] = "TopicWorld",
            ["Oldies"] = "TopicOldies"
        };

    /// <summary>Every identifier the publisher can emit, in the vocabulary's own order.</summary>
    public static IReadOnlyCollection<string> All { get; } = [.. Keys.Keys];

    /// <summary>
    /// The localization key naming <paramref name="topic"/>, or <c>null</c> when the value is blank or
    /// outside the closed set. Matching ignores case, so a hand-typed "pop" still gets its label; the
    /// stored value is never rewritten to the canonical spelling.
    /// </summary>
    public static string? ResourceKey(string? topic) =>
        !string.IsNullOrWhiteSpace(topic) && Keys.TryGetValue(topic.Trim(), out var key) ? key : null;
}
