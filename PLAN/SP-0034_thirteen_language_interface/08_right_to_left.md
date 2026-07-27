# Phase 08 - Right-to-left layout

**Status:** Implemented - code complete, visual confirmation deferred to phase 13

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

## Checks

- `rg -c UiFlowDirection` over the window XAML - expected: one binding per window | actual: 1 each in
  all eleven windows, including the new `LanguageWindow`.
- Transport exemption - expected: play and stop pinned `LeftToRight` | actual: two
  `FlowDirection" Value="LeftToRight"` setters in `App.xaml`, on `PlayGlyphButton` and `StopGlyphButton`.
- `AutomationProperties.Name` bindings in `MainWindow.xaml` - expected: not reduced by the margin edits |
  actual: 31, none removed.
- No localized label declares a fixed `Width` - expected: none | actual: none.
- `dotnet build StreamsPlayer.sln -c Release` - expected: succeeds | actual: succeeded, 0 warnings.

### Step 5 reassessed against the live layout

The plan assumed the toolbar would clip under German and Hindi. It will not: the toolbar sits in an
`Auto` column beside a `*` column whose only text (`MainSubtitle`) already carries
`TextTrimming="CharacterEllipsis"`, so expansion consumes the title area rather than overflowing.
Only one toolbar item bears text at all - the always-on-top checkbox - because every other control is a
glyph button. Restructuring the row into a `WrapPanel` would not even wrap inside an `Auto` column, and
would have been a speculative change with no visual evidence behind it. Left as is, with the German and
Hindi appearance recorded as an observed check in phase 13 instead.

### Why margins needed touching at all

WPF mirrors layout with `FlowDirection` but **not** `Margin` - margins stay physical. So
`Margin="12,0,0,0"` on the subtitle and `Margin="8,0,3,0"` on the checkbox would have put their gaps on
the wrong side under Arabic and Urdu while everything around them mirrored. Both are now symmetric.

This phase is the one part of the ticket that can fail visually with every automatic check green, which
is why its status stays short of proven until the Arabic and Urdu captures exist.
