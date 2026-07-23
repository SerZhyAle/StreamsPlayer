# PHASE-3 — App: custom actionable failure dialog wired to both failure paths

**Consumes:** Phase 1 (`FailureReportFormatter`, `PlaybackErrorCategory`, `CatalogUrlIdentity.Redact`), Phase 2 (`RemoveChannelAsync`).
**Produces:** the localized Retry / Remove / Copy-report / Keep dialog, replacing both dead-end `MessageBox` calls. Satisfies AC1, triggers AC2/AC3, delivers AC5's Copy action.

WPF `MessageBox` cannot express these four actions, so build a small dedicated dialog window (keep it UI-only, well under the ~500-line budget).

## Steps

1. **`PlaybackFailureDialog` window** — new `src/StreamsPlayer.App/PlaybackFailureDialog.xaml` (+ `.xaml.cs`).
   - Inputs: channel title, `SourceOrigin`, a lazily-produced report string (or the fields to format it).
   - Buttons: **Retry**, **Remove** (label bound to origin — `FailureHide` for Catalog, `FailureDelete` for Manual/Imported), **Copy report**, **Keep/Close**.
   - Result: expose `enum PlaybackFailureChoice { Retry, Remove, None }` (Copy is handled in-dialog and does not close it). Remove asks an inline confirmation before returning `Remove` (never-silent constraint); Keep/Close returns `None`.
   - Copy report → `Clipboard.SetText(FailureReportFormatter.Format(...))`; show a transient localized "report copied" acknowledgement. Copying never transmits.
   - Localization: add keys to **both** `Localization.en.xaml` and `Localization.ru.xaml` — `FailureDialogTitle`, `FailureDialogMessage`, `FailureRetry`, `FailureHide`, `FailureDelete`, `FailureCopyReport`, `FailureKeep`, `FailureConfirmDelete`, `FailureReportCopied`.

2. **Wire the video path** — `PlayerWindow` [PlayerWindow.xaml.cs:375](../../src/StreamsPlayer.App/PlayerWindow.xaml.cs#L375).
   - Add a constructor callback `Func<StreamChannel, Task> requestRemove` (mirror the existing `_recordOutcome`/`_saveTopmost` injection).
   - In `ShowPlaybackFailure`, when `notifyUser`, replace `MessageBox.Show` with `PlaybackFailureDialog`. Build the report via `FailureReportFormatter.Format` using app version + `DateTimeOffset.UtcNow` + `PlaybackErrorCategory.Classify(reason)`.
   - On `Retry` → `StartMedia("retry")`. On `Remove` → `await requestRemove(_channel)` then `Close()`. On `None` → keep current behaviour (leave the "unavailable" wait state).
   - Provide app version to the report (assembly informational version); keep the formatter Core-pure.

3. **Wire the audio path** — `MainWindow.AudioPlayer_MediaFailed` [MainWindow.xaml.cs:627](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L627).
   - Replace `MessageBox.Show` (line 636) with `PlaybackFailureDialog`. On `Retry` → re-invoke `PlayChannelAsync(row.Channel, rememberSelection: false)`. On `Remove` → `await RemoveChannelAsync(row.Channel)`. On `None` → unchanged.
   - Reason string for the classifier: the exception type name already logged at line 630.

4. **Inject the Remove callback into PlayerWindow** — `MainWindow.OpenIndependentPlayerWindow` [MainWindow.xaml.cs:658](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L658): pass `RemoveChannelAsync` as the new `requestRemove` argument.

## Static verification predicate

- `dotnet build StreamsPlayer.sln` succeeds with no new warnings.
- `rg "MessageBox.Show" src/StreamsPlayer.App/PlayerWindow.xaml.cs` returns **no** hit inside `ShowPlaybackFailure`; the audio `MessageBox.Show` at former line 636 is gone.
- `rg` confirms `PlaybackFailureDialog` is referenced from both `PlayerWindow` and `MainWindow`, and all nine new localization keys exist in **both** en and ru dictionaries (equal key counts).
- Record `expected: build ok; both paths use PlaybackFailureDialog; keys parity en==ru | actual: ...`.
- Interactive Retry/Remove/Copy behaviour is proven in Phase 5 GUI observation.

## Result — DONE

- New `PlaybackFailureDialog` (XAML + code-behind) with `PlaybackFailureChoice { None, Retry, Remove }`; Remove label is origin-aware (`FailureHide`/`FailureDelete`); the irreversible user-row delete is re-confirmed, hide is not; Copy report writes `FailureReportFormatter.Format(...)` to the clipboard and shows an inline ack.
- Video path: `PlayerWindow` gained a `Func<StreamChannel, Task> requestRemove` ctor callback; `ShowPlaybackFailure` now calls `ShowFailureDialog` (Retry→`StartMedia`, Remove→remove+`Close`). Audio path: `AudioPlayer_MediaFailed` shows the same dialog (Retry→`PlayChannelAsync`, Remove→`RemoveChannelAsync`). `OpenIndependentPlayerWindow` injects `RemoveChannelAsync`.
- Deviation from plan: reused existing `StreamUnavailableTitle` for the window/confirm title instead of adding `FailureDialogTitle`, so 8 new keys (not 9). Report version comes from `ProductInfo.Version`.
- expected: build clean; no MessageBox in ShowPlaybackFailure; dialog from both paths; key parity | actual: build 0/0; confirmed by grep; 8 `Failure*` keys in en and ru.
