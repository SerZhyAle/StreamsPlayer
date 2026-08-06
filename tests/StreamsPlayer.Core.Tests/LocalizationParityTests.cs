using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0034 criterion 2: the automatic gate that keeps thirteen dictionaries honest.
/// <para>
/// A missing key is a runtime defect here, not a build error - the lookup falls back to printing the
/// key itself - so with thirteen files divergence is near-certain without this. The sibling product
/// CyrFlip gets key parity from its compiler, because all thirteen translations of a string are
/// arguments of one call; keeping separate dictionaries means buying that guarantee back in tests.
/// </para>
/// <para>
/// Several complementary facts, plus <see cref="LocalizationGateSelfTests"/> proving the gate can fail.
/// A gate that silently inspected nothing would pass exactly as quietly as a clean one.
/// </para>
/// </summary>
public sealed class LocalizationParityTests
{
    private const string ReferenceCode = "en";

    /// <summary>
    /// Keys whose value is legitimately identical to English. Every entry needs a reason: this list is
    /// the one place an untranslated string can hide, so it must not be widened casually.
    /// </summary>
    private static readonly HashSet<string> SameAsEnglishAllowed =
    [
        .. new[]
        {
            // A protocol name, written the same way in every language we ship.
            "KindRtsp",
            // A composite format template, not prose.
            "WindowTitleWithSubject",
            // An enum token bound by XAML, not text; its own fact checks it against the registry.
            "UiFlowDirection",
            // The product name. docs/localization/glossary.md keeps the English brand in eleven of the
            // thirteen languages and localizes it only in Russian and Ukrainian, where "Трансляции" and
            // "Трансляції" are already published. Allow-listed rather than required: without this entry
            // the gate pushes a translator into inventing a brand variant, which is worse than a
            // duplicate string - it already produced "Player STREAMS" and "STREAMS 播放器" once.
            "ProductName",
            // A unit abbreviation used internationally.
            "BitrateValue",
            // Same shape: a number and an internationally written unit. Languages that do localize the
            // unit (Russian "кадр/с", Arabic "هرتز") simply do so; this only stops the gate demanding a
            // translation of "Hz" where "Hz" is what the language writes.
            "SampleRateValue",
            // Names a third-party library.
            "VideoBackendFlyleaf",
            "VideoBackendLibVlc"
        },
        // Endonyms are identical in all dictionaries on purpose: a picker that renamed a language when
        // you switched locale would be unusable for the person trying to get back.
        .. Enum.GetValues<AppLanguage>().Select(language => $"Language{language}")
    ];

    private static IReadOnlyList<LocalizationDictionary> All => LocalizationDictionary.LoadAll();

    private static LocalizationDictionary Reference =>
        All.Single(dictionary => dictionary.Code == ReferenceCode);

    [Fact]
    public void EveryShippedLanguageHasADictionaryAndEveryDictionaryIsShipped()
    {
        var onDisk = All.Select(dictionary => dictionary.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declared = InterfaceLanguages.All.Select(entry => entry.DictionaryCode).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = declared.Except(onDisk).Order().ToArray();
        var orphaned = onDisk.Except(declared).Order().ToArray();

        Assert.True(
            missing.Length == 0,
            $"Languages declared in InterfaceLanguages with no dictionary: {string.Join(", ", missing)}");
        Assert.True(
            orphaned.Length == 0,
            $"Dictionary files for languages that are not shipped: {string.Join(", ", orphaned)}");
    }

    [Fact]
    public void EveryDictionaryHoldsTheSameKeySetAsEnglish()
    {
        var reference = Reference;
        var problems = new List<string>();

        foreach (var dictionary in All.Where(dictionary => dictionary.Code != ReferenceCode))
        {
            foreach (var key in dictionary.Values.Keys.Except(reference.Values.Keys).Order())
            {
                problems.Add($"[{dictionary.Code}] key not in {ReferenceCode}: {key}");
            }

            foreach (var key in reference.Values.Keys.Except(dictionary.Values.Keys).Order())
            {
                problems.Add($"[{dictionary.Code}] missing key: {key}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void NoDictionaryDeclaresAKeyTwice()
    {
        var problems = new List<string>();
        foreach (var dictionary in All)
        {
            var duplicates = dictionary.KeysInFileOrder
                .GroupBy(key => key, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .Order();
            problems.AddRange(duplicates.Select(key => $"[{dictionary.Code}] duplicate key: {key}"));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryTranslationKeepsEnglishPlaceholders()
    {
        var reference = Reference;
        var problems = new List<string>();

        foreach (var dictionary in All.Where(dictionary => dictionary.Code != ReferenceCode))
        {
            foreach (var (key, englishValue) in reference.Values)
            {
                if (!dictionary.Values.TryGetValue(key, out var value))
                {
                    continue; // Reported by the key-set fact.
                }

                var expected = LocalizationDictionary.PlaceholderIndices(englishValue);
                var actual = LocalizationDictionary.PlaceholderIndices(value);
                if (!expected.SequenceEqual(actual))
                {
                    problems.Add(
                        $"[{dictionary.Code}] {key}: expected placeholders {{{string.Join(",", expected)}}}, " +
                        $"found {{{string.Join(",", actual)}}}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void NoDictionaryCarriesAByteOrderMark()
    {
        // A confirmed prior incident corrupted these exact files by reading BOM-less UTF-8 as CP1251 in
        // Windows PowerShell 5.1. This fact makes the resulting damage detectable instead of merely
        // unlikely - see memory/MEMORY.md.
        var offenders = All.Where(dictionary => dictionary.HasByteOrderMark)
            .Select(dictionary => dictionary.Code)
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0, $"Dictionaries with a BOM: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void LayoutDirectionAgreesWithTheCoreRegistry()
    {
        // Direction is carried as a dictionary key so XAML can bind it and it follows the runtime swap,
        // but InterfaceLanguages stays authoritative: this is what stops the two drifting.
        var problems = new List<string>();

        foreach (var entry in InterfaceLanguages.All)
        {
            var dictionary = All.SingleOrDefault(candidate =>
                string.Equals(candidate.Code, entry.DictionaryCode, StringComparison.OrdinalIgnoreCase));
            if (dictionary is null)
            {
                continue; // Reported by the coverage fact.
            }

            var expected = entry.RightToLeft ? "RightToLeft" : "LeftToRight";
            if (!dictionary.Values.TryGetValue("UiFlowDirection", out var actual))
            {
                problems.Add($"[{dictionary.Code}] has no UiFlowDirection key");
            }
            else if (actual.Trim() != expected)
            {
                problems.Add($"[{dictionary.Code}] UiFlowDirection is {actual.Trim()}, registry says {expected}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void NoTranslationIsLeftInEnglish()
    {
        // Two lists, because "identical to English" has two very different causes.
        //
        // SameAsEnglishAllowed covers keys that must be identical in every language - the brand, a
        // protocol name, a format template. LoanwordExceptions covers the opposite case: a key whose
        // correct rendering in one particular language happens to be the English word, because it is a
        // loanword there ("Audio" in Spanish, "Format" in German).
        //
        // The second list exists because an unconditional check does active harm. Faced with it, a
        // translator picks a worse synonym to get past the gate rather than the right word - this
        // already turned Spanish and French "Audio" into "Sonido" and "Son", and invented the brand
        // variants "Player STREAMS" and "STREAMS 播放器". Recording the exception per language keeps the
        // natural word AND keeps a genuinely skipped string failing.
        var exceptions = LoanwordExceptions.Load();
        var reference = Reference;
        var problems = new List<string>();

        foreach (var dictionary in All.Where(dictionary => dictionary.Code != ReferenceCode))
        {
            foreach (var (key, englishValue) in reference.Values)
            {
                if (SameAsEnglishAllowed.Contains(key) || !dictionary.Values.TryGetValue(key, out var value))
                {
                    continue;
                }

                // Values with no letters at all - separators, punctuation-only labels - cannot be
                // "untranslated" in any meaningful sense.
                if (!englishValue.Any(char.IsLetter))
                {
                    continue;
                }

                if (!string.Equals(value.Trim(), englishValue.Trim(), StringComparison.Ordinal))
                {
                    continue;
                }

                if (exceptions.IsAllowed(dictionary.Code, key))
                {
                    continue;
                }

                problems.Add(
                    $"[{dictionary.Code}] {key} is still English: \"{Trim(englishValue)}\". If that is the " +
                    $"correct word in this language, record it in {LoanwordExceptions.FileName} as " +
                    $"\"{dictionary.Code}:{key}\" with a reason - do not pick a worse synonym to pass this test.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryLoanwordExceptionIsStillNeeded()
    {
        // An exception that no longer describes reality is a hole in the gate. If the translation was
        // since changed, or the key or language removed, the entry has to go.
        var exceptions = LoanwordExceptions.Load();
        var reference = Reference;
        var stale = new List<string>();

        foreach (var (code, key) in exceptions.Entries)
        {
            var dictionary = All.SingleOrDefault(candidate =>
                string.Equals(candidate.Code, code, StringComparison.OrdinalIgnoreCase));
            if (dictionary is null)
            {
                stale.Add($"{code}:{key} - no such dictionary");
                continue;
            }

            if (!reference.Values.TryGetValue(key, out var englishValue))
            {
                stale.Add($"{code}:{key} - no such key in {ReferenceCode}");
                continue;
            }

            if (!dictionary.Values.TryGetValue(key, out var value)
                || !string.Equals(value.Trim(), englishValue.Trim(), StringComparison.Ordinal))
            {
                stale.Add($"{code}:{key} - no longer matches English, so the exception is unnecessary");
            }
        }

        Assert.True(stale.Count == 0, string.Join(Environment.NewLine, stale));
    }

    [Fact]
    public void NoValueIsEmpty()
    {
        var problems = new List<string>();
        foreach (var dictionary in All)
        {
            problems.AddRange(dictionary.Values
                .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => $"[{dictionary.Code}] {pair.Key} is empty"));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void NoDictionaryFetchesAnythingAtRuntime()
    {
        // SP-0034 criterion 15: everything ships inside the build. A Source attribute or an http URI in
        // a dictionary would be the only way a translation asset could arrive over the network.
        var problems = new List<string>();
        foreach (var dictionary in All)
        {
            // Values only, and only a real scheme separator. The root element's xmlns declarations are
            // schema URIs, and several strings legitimately name protocols in prose ("http, https, or
            // rtsp") - neither is something the app could fetch.
            problems.AddRange(dictionary.Values
                .Where(pair => pair.Value.Contains("://", StringComparison.Ordinal))
                .Select(pair => $"[{dictionary.Code}] {pair.Key} holds an absolute URI"));

            // A Source attribute is the only way a dictionary could pull in another one at runtime.
            if (File.ReadAllText(dictionary.Path).Contains("Source=", StringComparison.Ordinal))
            {
                problems.Add($"[{dictionary.Code}] declares a Source attribute");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheGateActuallyInspectedTheDictionaries()
    {
        // Without this, deleting every dictionary would make the whole class pass in silence.
        var all = All;
        Assert.Equal(InterfaceLanguages.All.Count, all.Count);
        Assert.All(all, dictionary => Assert.True(
            dictionary.Values.Count >= 100,
            $"[{dictionary.Code}] holds only {dictionary.Values.Count} keys"));
    }

    private static string Trim(string value) =>
        value.Length <= 60 ? value : value[..57] + "...";
}
