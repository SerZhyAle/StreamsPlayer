# PHASE-4 — Settings UI, persistence wiring, localized strings

**Produces:** engine ComboBox in the Settings window, persistence of `VideoBackend`, the value
threaded into `PlayerWindow`, EN+RU strings.
**Consumes:** Phase 1 (`MediaBackend`, `CatalogState.VideoBackend`), Phase 3 (`FlyleafVideoBackend`
selectable).
**Goal (AC 1, AC 5, AC 7, Decision 2):** Settings presents the choice with LibVLC pre-selected and
labelled default, Flyleaf labelled experimental fallback; the choice persists; a new player window
opens on the chosen engine.

## Steps

1. **Settings XAML — engine picker.** In
   [SettingsWindow.xaml](../../src/StreamsPlayer.App/SettingsWindow.xaml), inside the existing
   `PlaybackSettings` `GroupBox` (Grid.Row="1"), add below the two checkboxes:
   ```xml
   <TextBlock Text="{DynamicResource VideoBackendLabel}" FontWeight="SemiBold" Margin="0,12,0,0" />
   <ComboBox x:Name="VideoBackendBox" Margin="0,5,0,0" HorizontalAlignment="Stretch"
             AutomationProperties.Name="{DynamicResource VideoBackendLabel}" />
   <TextBlock Text="{DynamicResource VideoBackendHint}" Foreground="{StaticResource MutedBrush}"
              TextWrapping="Wrap" Margin="0,5,0,0" />
   ```
   Follows the `TileSizeBox` `UiOption` ComboBox pattern already in this file.

2. **Settings code-behind.** In
   [SettingsWindow.xaml.cs](../../src/StreamsPlayer.App/SettingsWindow.xaml.cs):
   - add a `MediaBackend videoBackend` constructor parameter;
   - populate `VideoBackendBox` with two `UiOption`s using the localized labels
     `VideoBackendLibVlc` (e.g. "VLC (default)") and `VideoBackendFlyleaf`
     (e.g. "FlyleafLib (experimental)"); select the one matching `videoBackend`;
   - add getter `public MediaBackend SelectedVideoBackend =>
     Enum.Parse<MediaBackend>(((UiOption)VideoBackendBox.SelectedItem).Value);`.

3. **MainWindow settings wiring.** In
   [MainWindow.Settings.cs](../../src/StreamsPlayer.App/MainWindow.Settings.cs):
   - pass `_state.VideoBackend` into the `new SettingsWindow(...)` call;
   - include `VideoBackend = dialog.SelectedVideoBackend` in the `_store.SaveAsync(_state with {…})`
     block. No live re-open needed — the choice takes effect on the next player window opened
     (document this: an already-open player keeps its engine).

4. **Thread into the player.** In
   [MainWindow.xaml.cs:838](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L838)
   `OpenIndependentPlayerWindow`, pass `_state.VideoBackend` as the new `PlayerWindow` backend
   argument (replacing the Phase-2 literal `MediaBackend.LibVlc`).

5. **Localized strings (EN + RU, no emoji).** Add to
   [Localization.en.xaml](../../src/StreamsPlayer.App/Localization.en.xaml) and
   [Localization.ru.xaml](../../src/StreamsPlayer.App/Localization.ru.xaml) keys:
   `VideoBackendLabel`, `VideoBackendHint` (frames Flyleaf as a fallback to try when a stream
   misbehaves on the default — Decision 2), `VideoBackendLibVlc`, `VideoBackendFlyleaf`. Keep both
   dictionaries key-aligned.

## Static check

- `dotnet build StreamsPlayer.sln -c Release` → **expected:** succeeds | **actual:** _record._
- Key-parity guard: every `VideoBackend*` key present in **both** `Localization.en.xaml` and
  `Localization.ru.xaml` (`rg "VideoBackend" src/StreamsPlayer.App/Localization.*.xaml`) →
  **expected:** same key set in each | **actual:** _record._
- **Run-and-observe (switch + persist):** open Settings → LibVLC pre-selected and labelled default;
  select FlyleafLib, Save; open a video stream → plays via Flyleaf; switch back to VLC → plays via
  VLC; restart the app → the last choice is still selected in Settings. Record `expected/actual` per
  step.
