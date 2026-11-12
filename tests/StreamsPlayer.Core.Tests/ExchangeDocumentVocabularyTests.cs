namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0224 requirement 2 beyond the interface strings: the documents that describe the exchange - the READMEs, the site
/// copy decks, the Store listing texts, the release notes and the portal contribution - use the dictionary's words, never the contract's
/// "relay" and "tunnel". The generated <c>docs/**/privacy.html</c> pages come from the decks, and
/// <c>tools/site/build-site.ps1 -Check</c> holds them to the decks.
/// </summary>
/// <remarks>
/// The history inside <c>msix/store-listing.md</c> is scanned with the rest: it carries no contract word.
/// </remarks>
public sealed class ExchangeDocumentVocabularyTests
{
    [Fact]
    public void ProductDocumentsNameTheExchangeWithTheDictionaryTermsOnly()
    {
        var surfaces = new (string Folder, string Pattern, int Minimum)[]
        {
            ("readme", "README*.md", 3),
            ("site-copy", "*.txt", LocalizationDictionary.LoadAll().Count),
            ("listing-copy", "*.txt", 13),
            ("msix-listing", "store-listing.md", 1),
            ("portal", "*.md", 3),
            ("release-notes", "*.txt", 1),
        };

        var problems = new List<string>();
        foreach (var (folder, pattern, minimum) in surfaces)
        {
            var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, folder), pattern);
            Assert.True(files.Length >= minimum, $"{folder}: expected at least {minimum} file(s) matching {pattern}, found {files.Length}.");
            foreach (var file in files)
            {
                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    foreach (System.Text.RegularExpressions.Match match in ExchangeVocabularyTests.TransportJargon.Matches(lines[index]))
                    {
                        problems.Add($"{folder}/{Path.GetFileName(file)}:{index + 1} '{match.Value}'");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0,
            "Contract words in a user-facing document (say 'directly connected' and 'exchange server' in the language's dictionary words): "
            + string.Join(", ", problems));
    }
}
