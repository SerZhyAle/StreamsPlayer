# Phase 02 - Resilient language state

**Status:** Implemented

Closes criteria 5 and 6 and audit finding A. Today an unknown `"language"` value throws
`JsonException` for the whole document (`StreamCatalogStore.cs:46`), the App leaves `_state` empty
(`MainWindow.xaml.cs:34,156-161`), and the first save path that ignores `_preferencesLoaded` commits
that empty state over the user's file - destroying `MANUAL`/`IMPORTED` rows and the favicon atlas.

The recovery must be at **field** level. A document-level `catch` returning `new CatalogState()`
would turn the crash into silent catalog loss and would not satisfy criterion 6.

1. Change `CatalogState.Language` in `src/StreamsPlayer.Core/Models.cs:212` to `AppLanguage?` with
   `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`, so "never chosen" is expressed by
   the property being absent rather than by `null`. Absence is a sound signal: every build so far
   serializes the field unconditionally, so a missing key means no build ever wrote a preference.
   Omitting rather than writing `null` also keeps an older build loadable, which a literal
   `"language": null` would break because its `AppLanguage` is not nullable.
   Static check: a saved state with `Language = null` contains no `language` property.

2. Add `src/StreamsPlayer.Core/TolerantAppLanguageConverter.cs`, a
   `JsonConverter<AppLanguage?>` that reads a case-insensitive member name, returns `null` for an
   unrecognised name, for a number outside the defined members, and for `JsonTokenType.Null`, and
   writes the member name. Register it in `StreamCatalogStore._jsonOptions` ahead of
   `JsonStringEnumConverter`. Never throw: an unreadable language is a preference we do not have,
   not a corrupt document.
   Static check: `_jsonOptions` lists the converter and the project builds.

3. Guard the write path independently of the converter. In
   `src/StreamsPlayer.App/MainWindow.xaml.cs`, set `_preferencesLoaded` only after a successful load
   (already true) and additionally refuse to save when it is `false`: add the guard to the paths that
   currently lack it - volume changes (`:501`, `:649`, `:893`, `:997`), add stream (`:253`), catalog
   refresh (`:195-197`), import (`MainWindow.ImportExport.cs:147`) and history
   (`MainWindow.NowPlaying.cs:93`). A failed load must never be able to overwrite the real file.
   Static check: `rg 'SaveAsync' src/StreamsPlayer.App` shows every call site behind
   `_preferencesLoaded` or inside the load itself.

4. Extend `tests/StreamsPlayer.Core.Tests/StreamCatalogStoreTests.cs` following the raw-JSON pattern
   at `CatalogStateHideTests.cs:41`: a state naming `"Hindi"` on an older enum, a state naming
   `"NotALanguage"`, and `{"language":99}` each load successfully with `Language == null` **and**
   with `Channels`, `Collections`, `MainWindowTopmost` and `TileSize` from the same file intact;
   a state written with `Language = AppLanguage.Arabic` round-trips; a state with no `language`
   property yields `null`.
   Static check: `dotnet test --filter FullyQualifiedName~StreamCatalogStoreTests` passes.

5. Park the general case rather than widening scope: `ViewMode`, `TileSize`, `VideoBackend` and
   `ChannelAccess` are deserialized by the same options and fail the same way. Record a Draft stub
   under `PLAN/` for tolerant enum handling across `CatalogState`, and do not fix it here.
   Static check: the stub ticket exists and this phase touches no enum other than `AppLanguage`.

## Checks

- `dotnet build StreamsPlayer.sln -c Release` - expected: succeeds | actual: succeeded, 0 warnings.
- `dotnet test --filter FullyQualifiedName~CatalogStateLanguageTests` - expected: passes | actual:
  19 passed, 0 failed.
- `dotnet test StreamsPlayer.sln -c Release` - expected: no regression | actual: 274 passed, 0 failed
  (255 before this phase).
- Saved state with `Language = null` - expected: no `language` property in the file | actual: asserted
  by `Save_OmitsTheLanguagePropertyWhenNoPreferenceIsHeld`.
- `rg '_store\.SaveAsync' src/StreamsPlayer.App` - expected: one hit, inside the gate | actual: one hit
  (`MainWindow.Localization.cs`, inside `PersistAsync`).
- `git diff --stat src/StreamsPlayer.App` after the scripted rewrite - expected: only the intended
  save-call lines change | actual: 26 single-line replacements across 14 files, no other content moved.
- Parked as `PLAN/SP-0035_tolerant_state_enums.md`.

### Deviation from step 3, recorded

The plan named eight save sites to guard individually. The live tree has **26** unguarded ones, so
guarding them by hand would have left the next new save path unprotected by default. Instead every
`_state = await _store.SaveAsync(..)` now routes through `MainWindow.PersistAsync`, which drops a save
taken before a successful load. The guard is structural rather than remembered, and the only direct
`_store.SaveAsync` call left is inside that helper.

### Design note on the nullable field

`Language` is omitted from the JSON when null rather than written as `null`. Writing `null` would have
broken criterion 5 in the other direction: a build predating SP-0034 has a non-nullable `AppLanguage`
and throws on an explicit `null`. Omission is also the correct signal, because every earlier build
serialized the property unconditionally - so an absent property genuinely means no build ever recorded
a preference, and a user who deliberately chose English is not mistaken for a fresh install.
