using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0039 criterion 12: the winget locale templates carry no forbidden term in their <c>Tags</c>.
/// <para>
/// The Store already refuses these terms in a search term (<c>tools/store/build-store-listing-csv.ps1</c>
/// fails the build); winget had no such gate, so <c>iptv</c> shipped in a tag after the owner had decided it
/// should not. The same list now binds both channels: a winget tag is a discoverability field like a Store
/// search term, and no product name and no piracy signal belongs in either. The templates and the list come
/// in as linked test data, read as text and never loaded, on the same terms as the site copy decks.
/// </para>
/// </summary>
public sealed partial class WingetTemplateTagTests
{
    // winget manifest schema 1.12.0: at most 16 tags, each 1-40 characters; policy 1.1.2: lowercase.
    // Hyphen is the one separator the existing tags use, so anything else (space, underscore) is a finding.
    private const int MaximumTags = 16;

    private static readonly string[] ExpectedLocales = ["en-US", "ru-RU", "uk-UA"];

    [Fact]
    public void EveryExpectedLocaleTemplateIsPresentAndDeclaresTags()
    {
        // A glob that quietly matched nothing, or a Tags block the parser no longer finds, would make the
        // checks below pass over an empty set.
        var locales = ReadLocaleTags();

        foreach (var locale in ExpectedLocales)
        {
            Assert.True(locales.ContainsKey(locale), $"winget template for {locale} is missing from the test data");
            Assert.True(locales[locale].Count > 0, $"[{locale}] no Tags block found, or it is empty");
        }
    }

    [Fact]
    public void NoTagContainsAForbiddenTerm()
    {
        var forbidden = ReadForbiddenTerms();
        Assert.NotEmpty(forbidden);

        var hits = ReadLocaleTags()
            .SelectMany(locale => ForbiddenHits(locale.Key, locale.Value, forbidden))
            .ToArray();

        Assert.True(hits.Length == 0, $"forbidden term in a winget tag: {string.Join(" | ", hits)}");
    }

    [Fact]
    public void TagsFitTheWingetShape()
    {
        var problems = ReadLocaleTags()
            .SelectMany(locale => ShapeProblems(locale.Key, locale.Value))
            .ToArray();

        Assert.True(problems.Length == 0, $"winget tag shape: {string.Join(" | ", problems)}");
    }

    [Fact]
    public void ForbiddenTermReaderFollowsTheStoreGate()
    {
        // build-store-listing-csv.ps1 Read-TermList: the term is the text before '#', trimmed; blank and
        // comment lines are skipped; a term may hold a space.
        var terms = ParseForbiddenTerms("# header\n\n  # indented comment\niptv   # why\nm3u playlist # why\r\nvlc\n");

        Assert.Equal(["iptv", "m3u playlist", "vlc"], terms);
    }

    [Fact]
    public void RealForbiddenListIsReadFromTheLinkedFile()
    {
        var terms = ReadForbiddenTerms();

        Assert.All(terms, term =>
        {
            Assert.False(string.IsNullOrWhiteSpace(term));
            Assert.DoesNotContain('#', term);
        });
    }

    [Fact]
    public void ASyntheticIptvTagIsCaught()
    {
        // The assertions above pass quietly when nothing matches, so prove they can fail - with the real list,
        // because the point is that the shipped list still rejects the tag this ticket removed.
        const string yaml = "PackageLocale: en-US\nTags:\n- radio\n- iptv\n- windows\nReleaseNotes: x\n";
        var tags = ParseTags(yaml);

        Assert.Equal(["radio", "iptv", "windows"], tags);

        var hits = ForbiddenHits("synthetic", tags, ReadForbiddenTerms());
        Assert.Single(hits);
        Assert.Contains("'iptv'", hits[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ASyntheticMalformedTagSetIsCaught()
    {
        var tooMany = Enumerable.Range(0, MaximumTags + 1).Select(i => $"tag-{i}").ToArray();
        Assert.NotEmpty(ShapeProblems("synthetic", tooMany));

        Assert.NotEmpty(ShapeProblems("synthetic", ["radio", "radio"]));
        Assert.NotEmpty(ShapeProblems("synthetic", ["Radio"]));
        Assert.NotEmpty(ShapeProblems("synthetic", ["live_tv"]));
        Assert.NotEmpty(ShapeProblems("synthetic", ["live tv"]));
        Assert.NotEmpty(ShapeProblems("synthetic", ["-radio"]));
        Assert.NotEmpty(ShapeProblems("synthetic", [new string('a', 41)]));

        Assert.Empty(ShapeProblems("synthetic", ["radio", "ip-camera", "ip-камера", new string('a', 40)]));
    }

    private static IReadOnlyList<string> ForbiddenHits(string locale, IEnumerable<string> tags, IReadOnlyList<string> forbidden) =>
        tags.SelectMany(tag => forbidden
                .Where(term => tag.ToLowerInvariant().Contains(term.ToLowerInvariant(), StringComparison.Ordinal))
                .Select(term => $"[{locale}] tag '{tag}' contains the forbidden term '{term}'"))
            .ToArray();

    private static IReadOnlyList<string> ShapeProblems(string locale, IReadOnlyList<string> tags)
    {
        var problems = new List<string>();

        if (tags.Count > MaximumTags)
        {
            problems.Add($"[{locale}] {tags.Count} tags, at most {MaximumTags} allowed");
        }

        problems.AddRange(tags.GroupBy(tag => tag, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => $"[{locale}] tag '{group.Key}' is repeated"));
        problems.AddRange(tags.Where(tag => !TagShape().IsMatch(tag))
            .Select(tag => $"[{locale}] tag '{tag}' is not 1-40 lowercase letters, digits and hyphens"));

        return problems;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadLocaleTags()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "winget-templates"), "*.locale.*.yaml");
        Assert.NotEmpty(files);

        return files.ToDictionary(
            path => LocaleOf(path),
            path => ParseTags(File.ReadAllText(path)));
    }

    // SerZhyAle.StreamsPlayer.locale.ru-RU.yaml -> ru-RU
    private static string LocaleOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name[(name.LastIndexOf('.') + 1)..];
    }

    // The block form the templates use: a "Tags:" line, then "- value" lines until the next key.
    private static IReadOnlyList<string> ParseTags(string yaml)
    {
        var tags = new List<string>();
        var inTags = false;

        foreach (var raw in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (!inTags)
            {
                inTags = line == "Tags:";
                continue;
            }

            if (!line.StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            tags.Add(line[2..].Trim());
        }

        return tags;
    }

    private static IReadOnlyList<string> ReadForbiddenTerms() =>
        ParseForbiddenTerms(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "msix-listing", "forbidden-terms.txt")));

    private static IReadOnlyList<string> ParseForbiddenTerms(string text)
    {
        var terms = new List<string>();

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var hash = trimmed.IndexOf('#', StringComparison.Ordinal);
            var term = (hash >= 0 ? trimmed[..hash] : trimmed).Trim();
            if (term.Length > 0)
            {
                terms.Add(term);
            }
        }

        return terms;
    }

    [GeneratedRegex(@"^[\p{Ll}\p{Nd}][\p{Ll}\p{Nd}-]{0,39}$")]
    private static partial Regex TagShape();
}
