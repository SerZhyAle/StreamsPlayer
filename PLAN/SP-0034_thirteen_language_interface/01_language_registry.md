# Phase 01 - Core language registry

**Status:** Implemented

Produces the single declaration required by Decision 3 and criterion 14. Today the set is asserted
in six places (`AppLanguage`, `LocalizationService.Available`, `DictionaryCode`, `CultureCode`,
`NativeName`, `ShortCode`) plus `ProductInfo.InstructionsUrl`; after this phase there is one.

1. Extend `AppLanguage` in `src/StreamsPlayer.Core/Models.cs` from three members to the thirteen in
   `INDEX.md`, in that order, keeping `English` at ordinal 0. Extend the existing SP-0029 comment to
   state that member names are the persisted tokens and must not be renamed.
   Static check: `AppLanguage` has 13 members and `English` is still first.

2. Add `src/StreamsPlayer.Core/InterfaceLanguages.cs` declaring
   `public sealed record InterfaceLanguage(AppLanguage Language, string DictionaryCode,
   string CultureCode, string ListingCode, bool RightToLeft)` and a static
   `InterfaceLanguages` exposing `All` (ordered, 13 entries), `For(AppLanguage)` and
   `Match(CultureInfo?)`. `Match` compares `TwoLetterISOLanguageName` against `DictionaryCode` and
   returns `null` when nothing matches - regional variants therefore resolve to their base language
   and an unshipped culture yields `null` rather than a guess. No `System.Windows` reference.
   Static check: `StreamsPlayer.Core.csproj` still has no WPF reference and the project builds.

3. Rewrite `src/StreamsPlayer.App/LocalizationService.cs` to derive from the registry: delete the
   `DictionaryCode`, `CultureCode` and `ShortCode` switch expressions, replace `Available` with
   `InterfaceLanguages.All`, and keep `NativeName` as a single lookup of the resource key
   `"Language" + language` so endonyms stay text in the dictionaries. Remove `ShortCode` entirely -
   phase 07 replaces the two-letter badge and nothing else consumes it.
   Static check: `rg 'AppLanguage\.(Russian|Ukrainian)' src/StreamsPlayer.App` returns no
   per-language switch arm.

4. Replace the three-arm switch in `src/StreamsPlayer.App/ProductInfo.cs:19-23`. The README set stays
   at three languages (criterion 13), so map `Russian` and `Ukrainian` to their README mirrors and
   every other language to the English README rather than inventing ten missing files.
   Static check: `InstructionsUrl` names exactly the three README files that exist on disk.

5. Add `tests/StreamsPlayer.Core.Tests/InterfaceLanguagesTests.cs`: `All` covers every enum member
   exactly once; codes are unique per surface; `Arabic` and `Urdu` are the only right-to-left
   entries; `Match` resolves each of the thirteen base cultures plus the regional variants `de-AT`,
   `pt-PT`, `es-MX`, `ar-EG`, `zh-Hant-TW`, and returns `null` for `pl-PL`, `ja-JP` and
   `CultureInfo.InvariantCulture`.
   Static check: `dotnet test --filter FullyQualifiedName~InterfaceLanguagesTests` passes.

## Checks

- `dotnet build StreamsPlayer.sln -c Release` - expected: succeeds, no new warning | actual:
  succeeded, 0 warnings, 0 errors.
- `dotnet test --filter FullyQualifiedName~InterfaceLanguagesTests` - expected: passes | actual:
  35 passed, 0 failed.
- `dotnet test StreamsPlayer.sln -c Release` - expected: no regression from widening the enum |
  actual: 255 passed, 0 failed (was 220 before this phase).
- `rg 'ShortCode|LocalizationService\.Available' src tools tests` - expected: no hit | actual: none.
- Step 4 needed no edit: `ProductInfo.InstructionsUrl` already ended in a `_ =>` English arm, so the
  ten new languages fall back to `README.md` without a code change. Verified the three README files
  it names all exist on disk.
- Deviation from step 3, recorded: `ShortCode` was removed, but the badge it fed
  (`MainWindow.Localization.cs:UpdateLanguageButton`) now derives its text from
  `InterfaceLanguages.For(..).DictionaryCode`. Deleting the badge outright belongs to phase 07;
  leaving a sixth per-language switch alive until then would have defeated the purpose of this phase.
