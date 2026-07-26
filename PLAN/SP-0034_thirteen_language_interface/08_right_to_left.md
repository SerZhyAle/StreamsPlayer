# Phase 08 - Right-to-left layout

**Status:** Approved

Decision 2 and criterion 3. `FlowDirection` and `RightToLeft` have **zero** source occurrences today,
so this starts from nothing. It is also the one part of the ticket that can fail visually while every
automatic check passes, which is why phase 13 requires an observed capture.

Direction is carried as a dictionary key rather than as code, so it follows the existing runtime swap
with no new plumbing, and the parity gate can check it against the Core registry (phase 05 step 2).

1. Each dictionary declares `<FlowDirection x:Key="UiFlowDirection">` - `RightToLeft` for `ar` and
   `ur`, `LeftToRight` for the other eleven (added in phase 06 step 2/3).
   Static check: the gate's `UiFlowDirection` assertion passes.

2. Set `FlowDirection="{DynamicResource UiFlowDirection}"` on the root element of every window:
   `MainWindow`, `PlayerWindow`, `SettingsWindow`, `AddStreamWindow`, `CollectionsWindow`,
   `ImportPreviewWindow`, `ImportUrlWindow`, `ListeningHistoryWindow`, `HiddenChannelsWindow`,
   `PlaybackFailureDialog`, and the new `LanguageWindow`. WPF propagates `FlowDirection` to children,
   so no per-control change is needed for ordinary content.
   Static check: `rg -c 'UiFlowDirection' src/StreamsPlayer.App/*.xaml` shows one hit per window.

3. Media transport controls stay direction-independent by convention (Decision 2). Pin the play,
   pause, stop, previous and next glyph containers to `FlowDirection="LeftToRight"` explicitly in
   `PlayerWindow.xaml`, so they do not mirror with the layout.
   Static check: the transport control group declares `LeftToRight` explicitly.

4. Asymmetric hardcoded margins become direction-neutral. The toolbar `StackPanel`
   (`MainWindow.xaml:28`) is right-aligned with `Margin="12,0,0,0"` (`:26`) and `Margin="8,0,3,0"`
   (`:39`); under mirroring these push the wrong way. Replace the asymmetric values with symmetric
   ones or move the spacing into the shared button style.
   Static check: no localized toolbar element carries an asymmetric horizontal margin.

5. Absorb the longest translation. German and Hindi expand well past the widths the toolbar row was
   tuned for, and that row packs the language button, a checkbox and two mode buttons into a tight
   right-aligned stack. Let the row wrap or give the labels `TextTrimming` with a tooltip carrying the
   full text; do not shrink the font.
   Static check: `dotnet build` succeeds and no localized label declares a fixed `Width`.

6. Keep automation names correct under mirroring: `AutomationProperties.Name` bindings are
   `DynamicResource` and follow the swap, so no change is required, but the 28 bindings in
   `MainWindow.xaml` must survive the margin edits.
   Static check: the `AutomationProperties` binding count in `MainWindow.xaml` is unchanged.
