# Research — SP-0008 Launch and resume

## Evidence

- `App.xaml` uses `StartupUri`, so command-line arguments are not currently
  routed to the main window; `App.xaml.cs` has no startup logic.
- `CatalogState` is JSON-persisted through `StreamCatalogStore` and already
  safely carries local UI preferences. It has no selected-channel field.
- `StreamChannel.Id` is a stable GUID persisted with every local record;
  `SortIndex` is a user-facing ordering value and is not an appropriate launch
  identifier.
- `MainWindow.Loaded` loads local state, applies localization, builds the
  catalog, and then starts grid previews. `Play(ChannelRow)` is the single
  routing point for audio versus video/RTSP playback.
- `StreamMediaKindClassifier` already classifies supported external URLs.
- `MainWindow` tracks a row selected by a card click, but does not expose any
  selected-channel action. The grid overflow menu currently exposes pinning.
- `PlayerWindow` records outcomes by channel GUID; an external URL needs a
  no-op outcome callback because it is intentionally not inserted into the
  local catalog.
- `StreamCatalogStoreTests` establish the persistence round-trip pattern.

## Decision

Use explicit options:

```text
StreamsPlayer.exe --url "https://example.test/live"
StreamsPlayer.exe --id "a persisted channel GUID"
```

`--id` is the database record identifier. A missing or invalid requested
target leaves the app open and shows a localized status rather than crashing.
An explicit argument takes precedence over resume. With no argument, the app
attempts the persisted last selected local channel; a missing prior channel is
ignored silently.

For a selected catalog row, a top-bar shortcut action offers Copy command and
Create desktop shortcut. Both use the durable `--id` form and target the
running application executable.

## Constraints and risks

- No launch path may implicitly refresh the catalog.
- Do not create a permanent catalog row for an external URL.
- Startup playback must use the existing offline gate and routing behaviour.
- Desktop shortcut creation is Windows-only and must report a local error if
  the shell refuses to create it.
