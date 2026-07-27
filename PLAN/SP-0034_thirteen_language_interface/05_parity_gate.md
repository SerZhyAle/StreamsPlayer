# Phase 05 - Parity gate

**Status:** Implemented

Criterion 2, and the Constraints requirement that the gate run in continuous integration rather than
by hand. CI already runs `dotnet test` on `windows-latest`, so a test is the cheapest host that is
actually enforced.

CyrFlip cannot be copied here. Its key parity is guaranteed by the compiler - all thirteen
translations of a string are arguments of one `Add(...)` call, so the sets cannot diverge. With
thirteen separate dictionaries that guarantee is gone, and parity, placeholder integrity and
untranslated-leftover detection are new code. What transfers is the *gate discipline*: several
complementary facts, and a self-test proving the gate is capable of failing.

The gate must not create a project reference. `StreamsPlayer.Core.Tests` may not reference
`StreamsPlayer.App`, so the dictionaries are consumed as **files** and parsed as XML.

1. Add to `tests/StreamsPlayer.Core.Tests/StreamsPlayer.Core.Tests.csproj` a `Content` item linking
   `..\..\src\StreamsPlayer.App\Localization.*.xaml` into `localization\` with
   `CopyToOutputDirectory="PreserveNewest"`. This is a test fixture, not a dependency: no App type is
   referenced and the dependency direction is unchanged.
   Static check: the test output directory holds one `Localization.*.xaml` per shipped language and
   `StreamsPlayer.Core.Tests.csproj` contains no `ProjectReference` to the App.

2. Add `tests/StreamsPlayer.Core.Tests/LocalizationParityTests.cs` asserting, with English as the
   reference:
   - every `InterfaceLanguages.All` entry has a dictionary file, and every dictionary file on disk
     belongs to a registry entry (no orphan file, no missing language);
   - each dictionary's `x:Key` set equals English's, reporting the symmetric difference;
   - no duplicate `x:Key` within a file;
   - per key, the multiset of format placeholder indices equals English's, so `{0}`/`{1}` cannot be
     dropped, duplicated or renumbered;
   - the file has no byte-order mark and parses as XML;
   - `UiFlowDirection` (phase 08) equals `RightToLeft` for exactly the registry's right-to-left
     languages and `LeftToRight` otherwise, which keeps the registry authoritative over the
     dictionaries;
   - no value is byte-identical to the English value outside an explicit allow-list - `KindRtsp`, the
     thirteen `Language*` endonyms, and any key whose English value is a proper noun or acronym. The
     allow-list is a named constant with a comment per entry, so a leftover cannot be hidden by
     quietly widening it.
   Static check: `dotnet test --filter FullyQualifiedName~LocalizationParityTests` passes.

3. Add the self-test in the same file, exercising the comparison helpers on synthetic dictionaries: a
   missing key, an extra key, a changed placeholder set, a duplicated key, a BOM-prefixed file and a
   value left in English each produce a failure. A gate that silently inspected nothing would pass
   exactly as quietly as a clean one.
   Static check: each synthetic case is asserted to be rejected.

4. Assert criterion 15 mechanically in the same test class: no dictionary declares a `Source`
   attribute or any `http` URI, so no translation asset can be fetched at runtime.
   Static check: the assertion exists and passes.

5. Do not touch `.github/workflows/ci.yml` - the gate is a test and the existing `dotnet test` step
   already runs it.
   Static check: `ci.yml` is unchanged and the gate appears in the test run output.

## Checks

- **End-to-end negative check on the real files.** Three defects were introduced into the shipped
  `Localization.de.xaml` at once - a deleted key, a dropped `{1}`, and a value reverted to English -
  then the gate was run and the file restored. Expected: all three named | actual:
  `[de] missing key: SortTopic`, `[de] ChannelCount: expected placeholders {0,1}, found {0}`,
  `[de] SortCountry is still English: "Country"`. This is the evidence criterion 2 asks for; the
  synthetic self-tests alone would not prove the gate reaches the shipped dictionaries.
- `dotnet test StreamsPlayer.sln -c Release` after restoring the file - expected: green | actual:
  299 passed, 0 failed.
- `dotnet test --filter FullyQualifiedName~LocalizationGateSelfTests` - expected: every synthetic defect
  is caught | actual: 12 passed - missing key, extra key, duplicated key, four kinds of placeholder
  change, BOM present and absent, value left in English, wrong layout direction, empty value, malformed
  XAML. `AFormatSpecifierChangeAloneIsNotAFailure` pins the deliberate tolerance.
- `dotnet test --filter FullyQualifiedName~LocalizationParityTests` on a tree with only three
  dictionaries - expected: fails naming the ten missing languages | actual: failed with
  `Languages declared in InterfaceLanguages with no dictionary: ar, bn, de, es, fr, hi, it, pt, ur, zh`.
  Recorded because it is the evidence that the gate is not passing vacuously.
- `StreamsPlayer.Core.Tests.csproj` - expected: no `ProjectReference` to the App | actual: none; the
  dictionaries arrive as linked `Content` and are parsed as XML.

### The untranslated-leftover fact had to be redesigned mid-phase

The first version failed a key whenever a translation equalled the English value, with a single global
allow-list. Two independent translators reported the same thing, and they were right: that rule does
active harm. Faced with it they did not add an exception, they picked a **worse word** -

- Spanish and French `Audio` became `Sonido` and `Son`, losing the standard UI term;
- Portuguese `STREAMS Player` became `Player STREAMS` and Chinese became `STREAMS 播放器`, inventing two
  brand variants that contradict `docs/localization/glossary.md`;
- Portuguese `{0} kbps` became `{0} kbit/s`, Chinese `{0} 千比特/秒`.

The gate was distorting the product to satisfy itself. Rebuilt as two lists with different jobs:

- `SameAsEnglishAllowed` - keys that must be identical in **every** language: `KindRtsp`,
  `WindowTitleWithSubject`, `UiFlowDirection`, the endonyms, `ProductName`, `BitrateValue`,
  `VideoBackendFlyleaf`, `VideoBackendLibVlc`.
- `tests/StreamsPlayer.Core.Tests/localization-loanwords.txt` - a per-language, per-key record with a
  reason, for the case where the correct word in **that** language is the English word. The failure
  message now names the file and says in as many words not to pick a worse synonym.

Running it against German and Italian, which had honestly kept their loanwords, produced 24 hits and
**every one was a genuine loanword** - Live, Audio, Video, Name, Format, Bitrate, Auto, Version,
Website, Media, Buffering, Privacy. Not one skipped string. That is the measurement that settled the
design.

`EveryLoanwordExceptionIsStillNeeded` keeps the list from becoming slack: an entry whose translation
later stops matching English, or whose key or language disappears, fails until the line is deleted.
