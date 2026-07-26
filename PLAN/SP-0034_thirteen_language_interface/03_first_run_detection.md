# Phase 03 - First-run language detection

**Status:** Approved

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
