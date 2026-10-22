using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0227: the privacy statement's exchange section, as far as it is readable from the page template, the
/// thirteen copy decks, the three READMEs and the Store listing - each one a claim that has to stay the same
/// claim everywhere without anything failing when it drifts.
/// <para>
/// What the exchange server's host can see is not ours to word: <c>DEVICE-EXCHANGE</c> item DE gives the
/// sentence in three languages and asks every product that describes the server to use it unreworded, and the
/// application already ships it as <c>ExchangeHonestText</c> in all thirteen dictionaries. The statement is
/// therefore held against that dictionary entry instead of a second copy of the words.
/// </para>
/// </summary>
public sealed partial class PrivacyStatementTests
{
    private const string HonestTextKey = "ExchangeHonestText";
    private const string HonestDeckKey = "privacy-exchange-honest";

    private static readonly string[] PromiseWords =
        ["coming soon", "planned", "not yet", "in development", "скоро", "планируется", "ещё не", "незабаром", "плануєтьс"];

    [Fact]
    public void EveryDeckSaysWhatTheServerSeesInTheApplicationsOwnWords()
    {
        var dictionaries = LocalizationDictionary.LoadAll().ToDictionary(dictionary => dictionary.Code, dictionary => dictionary.Values);

        foreach (var (code, deck) in ReadDecks())
        {
            Assert.True(dictionaries.TryGetValue(code, out var values), $"[{code}] the application has no dictionary for this site language");
            Assert.True(deck.TryGetValue(HonestDeckKey, out var onTheSite), $"[{code}] the deck has no {HonestDeckKey}");
            Assert.Equal(values![HonestTextKey], onTheSite);
        }
    }

    [Fact]
    public void ReadmesAndTheStoreListingCarryTheSameWords()
    {
        var dictionaries = LocalizationDictionary.LoadAll().ToDictionary(dictionary => dictionary.Code, dictionary => dictionary.Values);

        Assert.Contains(dictionaries["en"][HonestTextKey], ReadSurface("readme", "README.md"), StringComparison.Ordinal);
        Assert.Contains(dictionaries["ru"][HonestTextKey], ReadSurface("readme", "README.ru.md"), StringComparison.Ordinal);
        Assert.Contains(dictionaries["uk"][HonestTextKey], ReadSurface("readme", "README.uk.md"), StringComparison.Ordinal);
        Assert.Contains(dictionaries["en"][HonestTextKey], ReadSurface("msix-listing", "store-listing.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageHasTheExchangeSectionAndLinksTheAccountDeletionPage()
    {
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "site-templates", "privacy.html"));

        Assert.Contains("id=\"exchange\"", template, StringComparison.Ordinal);
        Assert.Contains("{{page.exchangeAccountDeletion}}", template, StringComparison.Ordinal);

        var keys = TemplateKeyPattern().Matches(template).Select(match => match.Groups[1].Value).Distinct().ToArray();
        Assert.Contains(HonestDeckKey, keys);

        foreach (var (code, deck) in ReadDecks())
        {
            var missing = keys.Where(key => !deck.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)).ToArray();
            Assert.True(missing.Length == 0, $"[{code}] privacy page key(s) missing or empty: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void TheInstallTrustPageSaysWhatThePrivacyIntroSays()
    {
        // INSTALL-TRUST rule 6: the "never does" list must not say more or less than the privacy statement.
        foreach (var (code, deck) in ReadDecks())
        {
            Assert.True(deck["privacy-intro"] == deck["trust-never-2"], $"[{code}] trust-never-2 differs from privacy-intro");
        }
    }

    [Fact]
    public void TheExchangeSectionIsWrittenAsShippedFacts()
    {
        // Canon invariant 17: a surface goes public with the release, so it never promises what is not built.
        var english = ReadDecks()["en"];
        foreach (var key in english.Keys.Where(key => key.StartsWith("privacy-exchange-", StringComparison.Ordinal)))
        {
            var offending = PromiseWords.Where(word => english[key].Contains(word, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.True(offending.Length == 0, $"{key} promises instead of stating a fact: {string.Join(", ", offending)}");
        }
    }

    private const string CameraDeckKey = "privacy-exchange-camera";
    private const string CameraButtonKey = "ExchangeImportCamera";

    [Fact]
    public void ThePackageDeclaresTheCameraExactlyAsLongAsEverySurfaceNamesTheButtonThatUsesIt()
    {
        // SP-0229 / canon rules 13 and 14: the manifest, the privacy page in every language, the READMEs and the Store text
        // say the same thing. The sentence is held to the button's own label in the application's dictionary, so a rename
        // of the button cannot leave a statement that names a control which no longer exists.
        var declared = DeclaresWebcam(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "msix", "AppxManifest.xml")));
        var dictionaries = LocalizationDictionary.LoadAll().ToDictionary(dictionary => dictionary.Code, dictionary => dictionary.Values);
        var decks = ReadDecks();
        var named = new List<string>();
        var missing = new List<string>();

        foreach (var (code, deck) in decks)
        {
            var label = dictionaries[code][CameraButtonKey];
            var has = deck.TryGetValue(CameraDeckKey, out var sentence) && sentence.Contains(label, StringComparison.Ordinal);
            (has ? named : missing).Add($"deck/{code}");
        }

        foreach (var (code, file) in new[] { ("en", "README.md"), ("ru", "README.ru.md"), ("uk", "README.uk.md") })
        {
            var has = ReadSurface("readme", file).Contains(decks[code][CameraDeckKey], StringComparison.Ordinal);
            (has ? named : missing).Add($"readme/{code}");
        }

        // The Store text speaks of "the user" where the page says "you", so it is held to the button's label and to its
        // own justification section rather than to the page's sentence word for word.
        var store = ReadSurface("msix-listing", "store-listing.md");
        var storeNames = store.Contains(dictionaries["en"][CameraButtonKey], StringComparison.Ordinal)
            && store.Contains("## webcam capability justification", StringComparison.Ordinal);
        (storeNames ? named : missing).Add("store-listing");

        if (declared)
        {
            Assert.True(missing.Count == 0, "The package declares the camera but these surfaces do not say it: " + string.Join(", ", missing));
        }
        else
        {
            Assert.True(named.Count == 0, "These surfaces name the camera but the package does not declare it: " + string.Join(", ", named));
        }
    }

    [Fact]
    public void TheCameraSentenceIsPartOfThePageAndStatesTheThreeGuarantees()
    {
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "site-templates", "privacy.html"));
        Assert.Contains("{{t." + CameraDeckKey + "}}", template, StringComparison.Ordinal);

        // The English sentence carries the three things a person deciding about the camera needs: when it is used, what
        // happens to the picture, and when it is let go.
        var english = ReadDecks()["en"][CameraDeckKey];
        foreach (var promise in new[] { "one thing only", "never stored, sent or logged", "released the moment the window closes" })
        {
            Assert.Contains(promise, english, StringComparison.Ordinal);
        }
    }

    private static bool DeclaresWebcam(string manifest) =>
        System.Xml.Linq.XDocument.Parse(manifest).Descendants()
            .Any(element => element.Name.LocalName == "DeviceCapability" && (string?)element.Attribute("Name") == "webcam");

    private static string ReadSurface(string folder, string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, folder, file));

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadDecks()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "site-copy"), "*.txt");
        Assert.NotEmpty(files);
        return files.ToDictionary(
            path => Path.GetFileNameWithoutExtension(path),
            path => ParseDeck(File.ReadAllText(path)));
    }

    // The deck format of tools/site/build-site.ps1: an "@@key" line, then the value until the next key.
    private static IReadOnlyDictionary<string, string> ParseDeck(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? key = null;
        var buffer = new List<string>();

        void Flush()
        {
            if (key is not null)
            {
                values[key] = string.Join("\n", buffer).Trim();
            }
        }

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                key = line[2..].Trim();
                buffer.Clear();
                continue;
            }

            if (key is not null)
            {
                buffer.Add(line);
            }
        }

        Flush();
        return values;
    }

    [GeneratedRegex(@"\{\{t\.([a-z0-9-]+)\}\}")]
    private static partial Regex TemplateKeyPattern();
}
