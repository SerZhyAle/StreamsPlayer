# SP-0020 Tactical Plan — Actionable failure dialog, hidden catalog channels, copyable reports

Strategic spec: [../SP-0020_hidden_channels_and_reports.md](../SP-0020_hidden_channels_and_reports.md) (Status: Tactical).

## Topology (dependency-ordered)

```
PHASE-1 (Core: hidden model + report/redaction + category + tests)
   └─> PHASE-2 (App: origin-aware Remove ops + ApplyFilter exclusion)
          ├─> PHASE-3 (App: custom actionable dialog wired to both failure paths)
          └─> PHASE-4 (App: manage-hidden window + unhide)
                 └─> PHASE-5 (Localization consolidation + full check + GUI observation)
```

Each phase's static-verification predicate must pass in its own run before the phase is done. Phases 3 and 4 both consume Phase 2; execute 3 then 4.

## Ground realities (verified in working tree)

- Video/RTSP failure: `PlayerWindow.ShowPlaybackFailure(reason, notifyUser)` — [PlayerWindow.xaml.cs:375](../../src/StreamsPlayer.App/PlayerWindow.xaml.cs#L375). Currently `MessageBox.Show` (OK). Records `_recordOutcome(_channel.Id, false)`. Retry primitive already exists: `StartMedia(reason)` [PlayerWindow.xaml.cs:112](../../src/StreamsPlayer.App/PlayerWindow.xaml.cs#L112).
- Audio failure: `MainWindow.AudioPlayer_MediaFailed` — [MainWindow.xaml.cs:627](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L627). Currently `MessageBox.Show` (OK). Records `RecordPlayOutcome(id, false)`.
- PlayerWindow is created in `MainWindow.OpenIndependentPlayerWindow` [MainWindow.xaml.cs:658](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L658) with callbacks (`RecordPlayOutcome`, `SavePlayerTopmostAsync`, …) — the injection point for a Remove callback.
- Display pipeline is one method: `MainWindow.ApplyFilter` [MainWindow.xaml.cs:289](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L289) — the single place hidden rows must be excluded. Row lifecycle via `_rowCache` + `GetOrCreateRow`.
- There is **no** existing channel-removal capability. Channels are only appended ([MainWindow.xaml.cs:226](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L226)); `ReplaceChannel` [MainWindow.xaml.cs:707](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L707) mutates in place.
- Persistence: `CatalogState` (record) [Models.cs:122](../../src/StreamsPlayer.Core/Models.cs#L122) saved whole via `StreamCatalogStore.SaveAsync` (atomic temp+move) — any new state field persists automatically. `JsonStringEnumConverter`, Web defaults ignore unknown members, so adding a defaulted field needs no SchemaVersion bump or migration.
- Merge keys catalog rows by exact `Url` (Ordinal) and only touches `SourceOrigin.Catalog` — [CatalogMerger.cs:11-22,70](../../src/StreamsPlayer.Core/CatalogMerger.cs#L11). Hide identity MUST use the same URL identity so a re-added catalog row re-matches its hidden entry. Merger stays untouched.

## Acceptance-criterion coverage map

| Criterion (from spec) | Phase(s) |
| --- | --- |
| AC1 actionable dialog (Retry/Remove/Keep), no dead-end OK, Retry re-attempts | P3 |
| AC2 Remove on Catalog = hide; survives restart + explicit refresh; no catalog-row delete/bank mutation | P1 (model+identity) · P2 (hide op + exclusion) · P3 (trigger) |
| AC3 Remove on Manual/Imported = delete; no reappear; no colliding-URL row touched | P2 (delete op) · P3 (trigger) |
| AC4 view/unhide hidden; absent-from-catalog cleaned without error; unhide preserves pin/order/collection/play-mark | P4 |
| AC5 copy bounded report (fields, no creds/paths); nothing transmitted; cancel = no-op | P1 (formatter+redaction+tests) · P3 (Copy button + clipboard) |
| AC6 merge/persistence/redaction/removal tests + localized retry/hide/delete/unhide/report run-observe | P1 (tests) · P2 (removal) · P5 (full check + GUI) |

## Constraint coverage

- Origin-aware, user-confirmed, never-silent Remove → P2 (routing) + P3 (confirm/dialog).
- Hidden excluded from views/search/counts/preview/navigation → P2 (`ApplyFilter`, counts, facets, preview queue).
- Manage-hidden view + unhide semantics → P4.
- Report fields + redaction + never transmit → P1 + P3.
- Core platform-neutral; explicit refresh + MANUAL/IMPORTED merge protection unchanged → P1 keeps `CatalogMerger` untouched; verified by existing merger tests still green in P1/P5.
