# PHASE-4 — Localization parity, docs, and validation

Consumes: Phase 3. Closes AC5/AC6 and sets the ticket status from reality.

## Steps

1. **Localization parity** — add every new key to **both**
   [Localization.en.xaml](../../src/StreamsPlayer.App/Localization.en.xaml) and
   [Localization.ru.xaml](../../src/StreamsPlayer.App/Localization.ru.xaml):
   `HistoryTitle`, `HistoryOpen`, `HistoryTip`, `HistoryEmpty`, `HistoryClear`,
   `HistoryPlayedAt` (`{0:g}` in both), `HistoryUnavailable`. Suggested en copy:
   Recently played / History / "See channels you recently played" / "No history yet." /
   Clear history / "Played {0:g}" / "This channel is no longer in your list.".
   Provide natural ru equivalents.

2. **User docs** — update the features list in
   [README.md](../../README.md) (and note the localized READMEs if they enumerate
   features) with one line: a private, local, bounded Recently-played history (last 100
   channels) surfaced from the toolbar, cleared on demand, never uploaded.

3. **Automated checks**
   - `dotnet build StreamsPlayer.sln -c Release` → 0 warnings.
   - `dotnet test StreamsPlayer.sln -c Release --no-build` → full suite green
     (prior 68 + new `ListeningHistoryTests`).
   - Key parity: the count of new `History*` keys is identical in en and ru
     (`rg -c "x:Key=\"History" src/StreamsPlayer.App/Localization.en.xaml` ==
     `... Localization.ru.xaml`).

4. **GUI run-and-observe** (record `expected | actual` each; a running user-owned Debug
   process → mark `BlockNeedUserTest` with these steps rather than killing it, per
   SP-0012/SP-0020 precedent):
   1. AC1 — play an audio channel to "Now playing"; open **History** → it is at the top
      with a local timestamp.
   2. AC2 — leave a Grid preview capturing and let a bad stream fail; History gains no
      entry for either.
   3. AC3 — replay an earlier channel → its existing row moves to top, no duplicate.
   4. AC4 — play an ICY station; after a title arrives, its History row shows the track
      text (presented as now-playing text, not a verified tag).
   5. AC5 — **Clear history** empties the list; catalog, pins, hidden set, and play
      marks are unchanged.
   6. AC6 — restart the app → history persists; delete a channel that has a row, reopen
      History → the row is a dimmed non-playable label and **Play** does nothing/soft
      status; switch EN⇄RU → window + button are localized.

5. **Status** — set [../SP-0019_listening_history.md](../SP-0019_listening_history.md)
   `**Status:**` to `Implemented` (or `Implemented — BlockNeedUserTest` with the exact
   GUI exit condition if step 4 could not be observed live), and add a short
   implementation summary. `Verified` only after step 4 is observed with zero failures.

## Static verification predicate

- Build 0 warnings; full test suite green; en/ru `History*` key counts equal.
- Record `expected: build 0 warn; suite green; key parity; GUI AC1–AC6 observed | actual: ...`.

## Result — Implemented; GUI observation blocked on user

- en/ru `History*` key parity: 8 == 8. README "What it does" gained a Recently-played
  bullet and names listening history in the local-storage bullet.
- expected: build 0 warn; suite green; key parity | actual: solution build 0 warn; full
  suite 136/136; parity 8==8.
- Startup smoke launch (Release exe): process alive 7 s, no new `Current.log` lines →
  no startup XAML/resource exception (toolbar History button + glyph template parse OK).
- AC1–AC6 interactive run-and-observe (play→top-with-timestamp, previews/failures create
  nothing, replay updates in place, ICY row text, Clear leaves catalog/pins/marks intact,
  restart persistence, deleted-channel non-playable label + soft-fail, EN⇄RU) require a
  human UI session → BlockNeedUserTest. Exit: user confirms the six checks.
