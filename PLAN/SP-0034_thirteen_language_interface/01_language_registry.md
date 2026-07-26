# Phase 01 - Core language registry

**Status:** Approved

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
