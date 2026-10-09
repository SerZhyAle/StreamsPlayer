using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0039: the support page, as far as it is readable from its template and the thirteen copy decks.
/// <para>
/// Three things go wrong on a page like this without anything failing: a section a reader was sent to by its
/// id moves or disappears, the contact address gets typed into a deck instead of rendered from the one
/// placeholder every page shares, and the page tells a reader to press a control by a name the application
/// does not show. The last one is the expensive one - a machine-translated deck can say "Settings" in a
/// word the app never uses - so the controls are held against the application's own dictionaries, which
/// come in as linked test data for the localization gates (read as XML, never loaded).
/// </para>
/// </summary>
public sealed partial class SupportPageTests
{
    private static readonly string[] SectionIdsInOrder =
        ["support-bug", "support-logs", "support-stream", "support-warning", "support-private", "support-response"];

    [Fact]
    public void TemplateCarriesTheSixSectionsInOrder()
    {
        var template = ReadTemplate();

        var positions = SectionIdsInOrder
            .Select(id => (Id: id, Index: template.IndexOf($"id=\"{id}\"", StringComparison.Ordinal)))
            .ToArray();

        Assert.All(positions, section => Assert.True(section.Index >= 0, $"support.html has no section id=\"{section.Id}\""));
        Assert.Equal(SectionIdsInOrder, positions.OrderBy(section => section.Index).Select(section => section.Id));

        // The prefix belongs to the sections alone: a seventh id carrying it would be an anchor nobody pinned.
        var carrying = SupportIdPattern().Matches(template).Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(SectionIdsInOrder.OrderBy(id => id, StringComparer.Ordinal), carrying.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryDeckRendersTheContactAddressFromThePlaceholder()
    {
        foreach (var (code, deck) in ReadDecks())
        {
            Assert.True(deck.TryGetValue("support-private", out var privateText) && privateText.Contains("[[email]]", StringComparison.Ordinal),
                $"[{code}] support-private does not carry the [[email]] placeholder");

            // The address changes in one place per surface; a typed one would stay behind when it does.
            var typed = deck
                .Where(entry => entry.Key.StartsWith("support-", StringComparison.Ordinal) || entry.Key.StartsWith("whatsnew-", StringComparison.Ordinal))
                .Where(entry => entry.Value.Contains('@') || entry.Value.Contains("mailto:", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Key)
                .ToArray();
            Assert.True(typed.Length == 0, $"[{code}] key(s) type an address instead of using [[email]]: {string.Join(", ", typed)}");
        }
    }

    [Fact]
    public void EveryDeckNamesTheSettingsAndLogControlsWithTheAppsOwnWords()
    {
        // The logs section is the landing's own how-to (use-5), reused rather than written a second time. It sends
        // the reader to Settings by two routes - the gear button, and the Operations menu, whose Library settings
        // entry opens the same window - and ends on the About page's Send logs button, so it names all of them. It
        // also says where the archive lands: the Frames folder setting (FrameFolderLabel) on the Files page
        // (SettingsFiles). The bug section names Settings and the About page it opens on. Each is the string that
        // language's dictionary shows on the control, not a fresh translation of it.
        var problems = new List<string>();
        foreach (var (code, deck, app) in DecksWithDictionaries())
        {
            RequireNames(problems, code, deck, app, "use-5-text",
                "Settings", "Operations", "LibrarySettings", "SendLogs", "SettingsAbout", "FrameFolderLabel", "SettingsFiles");
            RequireNames(problems, code, deck, app, "support-bug", "Settings", "SettingsAbout");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryDeckNamesTheFailureWindowControlsWithTheAppsOwnWords()
    {
        // A hidden channel comes back by one route the section spells out: the Hidden button on the Library page
        // of Settings (ManageHiddenOpen, SettingsLibrary), then Unhide next to the channel. A deck that sends the
        // reader to a control by a word the app never shows leaves a hidden channel with no way back.
        var problems = new List<string>();
        foreach (var (code, deck, app) in DecksWithDictionaries())
        {
            RequireNames(problems, code, deck, app, "support-stream",
                "StreamUnavailableTitle", "FailureRetry", "FailureCopyReport", "FailureHide", "FailureDelete", "FailureKeep",
                "Operations", "UpdateCatalog", "SettingsLibrary", "ManageHiddenOpen", "Unhide");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ControlNameCheckCanFail()
    {
        // The gates above pass quietly when every name is present, so prove one can fail - and that the
        // typographic apostrophe a deck sets is the same word as the straight one a dictionary holds.
        var problems = new List<string>();
        var deck = new Dictionary<string, string> { ["use-5-text"] = "Open Preferences and press Send the logs." };
        var app = new Dictionary<string, string> { ["Settings"] = "Settings", ["SendLogs"] = "Send logs to the author" };
        RequireNames(problems, "xx", deck, app, "use-5-text", "Settings", "SendLogs");
        Assert.Equal(2, problems.Count);

        problems.Clear();
        RequireNames(problems, "xx",
            new Dictionary<string, string> { ["k"] = "Press «Envoyer les journaux à l’auteur»." },
            new Dictionary<string, string> { ["SendLogs"] = "Envoyer les journaux à l'auteur" },
            "k", "SendLogs");
        Assert.Empty(problems);
    }

    private static void RequireNames(
        List<string> problems,
        string code,
        IReadOnlyDictionary<string, string> deck,
        IReadOnlyDictionary<string, string> app,
        string deckKey,
        params string[] appKeys)
    {
        if (!deck.TryGetValue(deckKey, out var text))
        {
            problems.Add($"[{code}] deck has no {deckKey}");
            return;
        }

        foreach (var appKey in appKeys)
        {
            if (!app.TryGetValue(appKey, out var name) || string.IsNullOrWhiteSpace(name))
            {
                problems.Add($"[{code}] Localization.{code}.xaml has no {appKey}");
            }
            else if (!SameApostrophe(text).Contains(SameApostrophe(name), StringComparison.Ordinal))
            {
                problems.Add($"[{code}] {deckKey} does not name the control \"{name}\" ({appKey})");
            }
        }
    }

    // A deck sets the typographic apostrophe the house style asks for; a dictionary holds the straight one.
    private static string SameApostrophe(string text) => text.Replace('’', '\'');

    private static IEnumerable<(string Code, IReadOnlyDictionary<string, string> Deck, IReadOnlyDictionary<string, string> App)> DecksWithDictionaries()
    {
        var decks = ReadDecks();
        var dictionaries = LocalizationDictionary.LoadAll().ToDictionary(dictionary => dictionary.Code, StringComparer.Ordinal);

        Assert.Equal(dictionaries.Keys.OrderBy(code => code, StringComparer.Ordinal), decks.Keys.OrderBy(code => code, StringComparer.Ordinal));
        return decks.Select(entry => (entry.Key, entry.Value, dictionaries[entry.Key].Values));
    }

    private static string ReadTemplate() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "site-templates", "support.html"));

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadDecks()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "site-copy"), "*.txt");
        Assert.NotEmpty(files);
        return files.ToDictionary(
            path => Path.GetFileNameWithoutExtension(path),
            path => ParseDeck(File.ReadAllText(path)));
    }

    // The deck format of tools/site/SiteDecks.ps1: an "@@key" line, then the value until the next key.
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

    [GeneratedRegex("""\bid="(support-[a-z0-9-]*)""")]
    private static partial Regex SupportIdPattern();
}
