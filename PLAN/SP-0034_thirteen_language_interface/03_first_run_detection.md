# Phase 03 - First-run language detection

**Status:** Implemented

Closes criterion 4 and Decision 5. Depends on phase 02: without `AppLanguage?` there is no way to
tell "no preference" from "chose English", so detection could not honour a saved choice.

1. Add `InterfaceLanguages.Detect(CultureInfo? display, CultureInfo? installed)` to
   `src/StreamsPlayer.Core/InterfaceLanguages.cs`, returning `Match(display) ?? Match(installed) ??
   AppLanguage.English`. The user's display language is tried before the language Windows was
   installed in. Cultures are parameters, not ambient state, so the matrix is testable on any machine.
   Static check: the method is pure and takes both cultures as arguments.

2. In `src/StreamsPlayer.App/MainWindow.xaml.cs`, immediately after the successful load at `:126`,
   resolve the effective language as `_state.Language ?? InterfaceLanguages.Detect(
   CultureInfo.CurrentUICulture, CultureInfo.InstalledUICulture)` and pass that to
   `LocalizationService.Apply`. Persist the detected value once, so the second launch is a normal
   saved-preference launch. Never write when `_state.Language` already has a value.
   Static check: `rg 'Detect\(' src/StreamsPlayer.App` shows exactly one call site, on the
   null-preference branch.

3. `LocalizationService.Apply` currently sets only `CultureInfo.CurrentUICulture`
   (`LocalizationService.cs:33`). Also set `CultureInfo.DefaultThreadCurrentUICulture` so worker
   threads format text in the selected language. Do **not** set `CurrentCulture`: catalog parsing and
   the persisted state must keep their existing invariant behaviour, and phase 04 removes the two call
   sites that depend on the ambient culture.
   Static check: `rg 'CurrentCulture' src/StreamsPlayer.App` shows no assignment to `CurrentCulture`.

4. Add `tests/StreamsPlayer.Core.Tests/InterfaceLanguagesTests.cs` cases: each of the thirteen
   display cultures resolves to its own language; an unshipped display culture falls through to the
   installed culture; both unshipped yields `English`; `null`/`null` yields `English`; and the result
   is always a member of `InterfaceLanguages.All`.
   Static check: `dotnet test --filter FullyQualifiedName~InterfaceLanguagesTests` passes.

## Checks

- `rg 'InterfaceLanguages.Detect' src` - expected: exactly one call site, on the null-preference
  branch | actual: one hit, `MainWindow.xaml.cs:132`, guarded by `savedLanguage ??`.
- `rg 'CultureInfo.CurrentCulture *=|DefaultThreadCurrentCulture *=' src` - expected: no hit | actual:
  none. Only `CurrentUICulture` and `DefaultThreadCurrentUICulture` are assigned, in
  `LocalizationService.Apply`.
- `dotnet test --filter FullyQualifiedName~InterfaceLanguagesTests` - expected: the culture matrix
  passes | actual: 35 passed, 0 failed, including all thirteen display cultures, fall-through to the
  installed culture, both-unshipped, and `Detect_AlwaysReturnsAShippedLanguage` over every neutral
  culture the machine knows.
- `Detect` is pure and takes both cultures as arguments, so the matrix does not depend on the machine's
  own locale.

Interaction with phase 02 worth recording: the detected value is persisted through `PersistAsync`
immediately after `_preferencesLoaded` is set, so a fresh install writes its language exactly once and
every later launch is an ordinary saved-preference launch. A later change to the operating system
language therefore cannot silently move an established user's interface - which is what criterion 4
means by "an existing saved preference is preserved".
