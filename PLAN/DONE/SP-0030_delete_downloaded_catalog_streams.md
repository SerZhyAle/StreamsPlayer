# SP-0030: Delete all downloaded catalog streams

**Status:** Verified

Tactical plan: [SP-0030_delete_downloaded_catalog_streams/INDEX.md](SP-0030_delete_downloaded_catalog_streams/INDEX.md)

## Implementation notes (SP-0030)

- `src/StreamsPlayer.Core/CatalogPurge.cs` (new) - `CountDownloaded` and `RemoveDownloaded`;
  only `SourceOrigin.Catalog` rows are dropped, the rest of `CatalogState` rides through.
- `src/StreamsPlayer.Core/Models.cs` - `CatalogPurgeResult(State, RemovedChannelIds)`.
- `tests/StreamsPlayer.Core.Tests/CatalogPurgeTests.cs` (new) - 4 tests.
- `src/StreamsPlayer.App/MainWindow.CatalogPurge.cs` (new) - count → zero-case message →
  Yes/No confirmation with the count → save → `ForgetRow` per removed id → facets/filter →
  `CATALOG PURGE` log event → status.
- `MainWindow.ImportExport.cs` - `StreamListPortabilityAction`/`RunStreamListPortabilityAsync`
  renamed to `StreamListAction`/`RunStreamListActionAsync` (the enum now also carries a
  non-portability action); `SettingsWindow.xaml.cs` and `MainWindow.Settings.cs` follow.
- `MainWindow.xaml.cs` - the Hidden button now follows rows actually hidden right now, not the
  raw `HiddenCatalogUrls` count, so a surviving hide identity cannot open an empty window.
- `SettingsWindow.xaml` + `App.xaml` (`TrashGlyphButton`) - separated destructive row at the
  bottom of the Playlists tab; `Localization.en/ru.xaml` - 7 keys each.
- `README.md`, `README.ru.md`, `README.uk.md` - one feature bullet each.

Static checks: `dotnet build StreamsPlayer.sln -c Debug` → expected 0 errors | actual 0 errors,
0 warnings. `dotnet test StreamsPlayer.sln -c Debug` → expected all green | actual 153/153 pass.

## Verification (Phase 5, run-and-observe, Debug build, RU UI)

Baseline state: 2360 Catalog + 1 Manual rows, 1 hidden identity, 7 pinned, 97 outcome marks,
10 history entries. State backed up to `tmp/SP-0030/` before the run and restored after.

1. Settings → Playlists shows the hint, separator, and **Удалить загруженные трансляции**
   (`tmp/SP-0030/settings-playlists.png`) - expected: visible, separated | actual: as expected.
2. Click → confirmation "Удалить все загруженные из каталога трансляции (2,360)?" with the
   keep-your-channels and cannot-be-undone wording (`tmp/SP-0030/confirm.png`) -
   expected: count and warning | actual: as expected.
3. Decline (No) - expected: state unchanged | actual: total=2361, catalog=2360 unchanged.
4. Accept (Yes) - expected: catalog rows gone, user row kept, other state kept | actual:
   total=1, catalog=0, manual=1, hidden identity=1, atlas and last-refresh time preserved;
   status "Удалено загруженных трансляций: 2,360."; Hidden button auto-collapsed
   (`tmp/SP-0030/main-after.png`).
5. Restart the app - expected: still purged | actual: 1 list row, no Hidden button, status
   still reports the last catalog refresh time.
6. **Обновить каталог** - expected: catalog rows return | actual: total=3244, catalog=3243,
   manual row untouched.
7. Zero-case: with a user-rows-only state, the button reports "Загруженных трансляций каталога
   нет." and changes nothing (`tmp/SP-0030/none-dialog.png`) - expected | actual: as expected.

Known cosmetic behaviour (not a defect of this ticket): the Yes/No captions come from Windows,
so they stay English under a Russian UI, matching the existing export-warning dialog.

## Goal

Give the user one confirmed action in Settings that removes every stream that came from the
downloaded catalog, leaving only the channels the user owns - manually added RTSP/HTTP entries
and M3U-imported rows.

## Why

The shared stream bank is large. A user who runs StreamsPlayer for a handful of personal RTSP
cameras or a small hand-built list has no way to get rid of the downloaded rows short of hiding
them one by one or deleting local state by hand. Hiding is per-channel and keeps the rows in
state; wiping `%LOCALAPPDATA%` also destroys the user's own channels, pins, and history. A single
explicit, confirmed "remove what was downloaded" action closes that gap and matches the product's
explicit-refresh stance: the catalog arrives only when asked for, and it can be dismissed the
same way.

## Non-goals

- Not a catalog-refresh change. Refresh stays explicit and unchanged; a later **Update catalog**
  legitimately downloads the rows again.
- Does not touch `MANUAL` or `IMPORTED` rows, pins on user rows, listening history, previews,
  or any preference.
- No "undo" and no recycle bin. The confirmation is the only guard.
- Not a per-channel or filtered delete. Scope is all-or-nothing over catalog rows; per-channel
  removal already exists (hide for catalog rows, delete for user rows).
- No new automatic behaviour: nothing deletes catalog rows on its own, on a schedule, or at
  startup.

## Decisions

1. **Scope is `SourceOrigin.Catalog` only.** Rows the user typed in (`Manual`) or imported from
   M3U (`Imported`) are never removed, including catalog rows the user later edited - editing a
   catalog row already re-stamps it `Manual`.
2. **Confirmation is required and states the count.** The prompt names how many downloaded
   streams will go, says user channels are kept, and says the action cannot be undone.
3. **Applies immediately on confirm.** Like the existing import/export actions, the confirmation
   is the commit point; closing Settings with Cancel does not resurrect the rows.
4. **The hidden-channel list survives.** Hidden identities are a user preference about catalog
   rows, not rows themselves; keeping them means a later refresh restores the user's hide choices
   instead of flooding the list with channels they already dismissed.
5. **Nothing is deleted from disk beyond the rows.** The favicon atlas and the recorded last
   refresh time stay; they describe the last download, which still happened.
6. **Placement: the Playlists (M3U) tab in Settings**, visually separated from import/export as
   the destructive action of that group.

## Constraints

- The removal rule lives in `StreamsPlayer.Core` and is unit-tested; the App only confirms,
  invokes, and reports.
- Persistence goes through the existing atomic state save; a failed save must leave the list
  unchanged.
- Any removed row that is selected or currently playing must be released from the UI, not left
  as a dangling reference.
- Strings are localized in English and Russian; no emoji.

## Acceptance criteria

1. Settings shows a **Delete downloaded streams** action that asks for confirmation before doing
   anything, and does nothing when the user declines.
2. Confirming removes every `SourceOrigin.Catalog` row and leaves every `Manual`/`Imported` row,
   with their pins, intact.
3. When there are no catalog rows, the action explains that and changes nothing.
4. The change is persisted: after restarting the app, the catalog rows are still gone and the
   user's channels are still there.
5. A later explicit **Update catalog** downloads the catalog rows again - the action is not a
   permanent opt-out.
6. Listening history, previews, and preferences are unaffected.
7. Core unit tests cover the removal rule; `./build.ps1 -Test` is green; the flow is verified by
   run-and-observe with `expected | actual` evidence.

## Risks

- Destructive and irreversible for the current state file: an accidental click must be caught by
  the confirmation, so the prompt must be explicit about count and permanence.
- A user could read "downloaded" as "everything imported"; the confirmation text must say user
  channels are kept.
