using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0061: the closed rubric vocabulary and the gate that keeps it in step with the dictionaries.
/// </summary>
public sealed class CatalogTopicsTests
{
    /// <summary>
    /// The vocabulary as the live bank spelled it on 2026-08-07 (19855 rows, 31 distinct values, no
    /// blanks). Written out rather than derived from <see cref="CatalogTopics"/>, so a silent edit to
    /// the registry - a corrected spelling, a dropped rubric - fails here instead of quietly changing
    /// which rows get a label. <c>Test</c> is declared by the publisher and currently matches zero rows.
    /// </summary>
    private static readonly string[] Published =
    [
        "General", "Pop", "Rock", "Electronic", "Traffic cams", "Religious", "Oldies", "News", "World",
        "Jazz & Blues", "Country & Folk", "Hip-hop", "Chillout", "Metal", "Kids", "Latin", "Talk",
        "Sports", "Movies & Series", "Classical", "R&B & Soul", "Webcam", "Local radio", "Reggae",
        "Comedy", "Education", "Documentary", "Adult", "Business", "Shopping", "Lifestyle", "Test"
    ];

    [Fact]
    public void TheRegistryHoldsExactlyThePublishedVocabulary()
    {
        Assert.Equal(
            Published.OrderBy(topic => topic, StringComparer.Ordinal),
            CatalogTopics.All.OrderBy(topic => topic, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryRubricHasItsOwnKey()
    {
        var keys = CatalogTopics.All.Select(CatalogTopics.ResourceKey).ToArray();

        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(key)));
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("Jazz & Blues", "TopicJazzBlues")]
    [InlineData("Traffic cams", "TopicTrafficCams")]
    [InlineData("R&B & Soul", "TopicRnbSoul")]
    // Case and surrounding space come from a hand-typed rubric on a manually added channel. The label
    // is still the right one; the stored value is deliberately left as the user wrote it.
    [InlineData("pop", "TopicPop")]
    [InlineData("  News  ", "TopicNews")]
    public void AKnownRubricResolvesToItsKey(string topic, string expected) =>
        Assert.Equal(expected, CatalogTopics.ResourceKey(topic));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Sea shanties")]
    [InlineData("Radio")] // A category, not a rubric. The two vocabularies must not bleed into each other.
    public void AnythingElseResolvesToNull(string? topic) =>
        Assert.Null(CatalogTopics.ResourceKey(topic));

    [Fact]
    public void EveryRubricKeyExistsInEveryDictionary()
    {
        // Without this, adding a rubric to Core and forgetting its thirteen labels is a runtime defect:
        // the lookup falls back to printing the key name, so "TopicShopping" would appear in the filter.
        // The parity gate cannot catch it on its own - it compares dictionaries with each other, and
        // thirteen files missing the same key are in perfect agreement.
        var problems = new List<string>();
        foreach (var dictionary in LocalizationDictionary.LoadAll())
        {
            problems.AddRange(CatalogTopics.All
                .Select(CatalogTopics.ResourceKey)
                .Where(key => key is not null && !dictionary.Values.ContainsKey(key))
                .Select(key => $"[{dictionary.Code}] missing rubric label: {key}"));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
