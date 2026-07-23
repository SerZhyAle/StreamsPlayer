# Phase 03 — Card technical-details display

**Produces:** a compact per-card technical-details line shown only when at least
one field is present, with one quiet fallback; localized labels.
**Consumes:** Phase 01 `StreamChannel` fields.

## Steps

1. [ChannelRow.cs](../../src/StreamsPlayer.App/ChannelRow.cs): add
   `public string TechnicalDetails` that joins, with `"  ·  "`, the present
   values in order: `Format` (upper-cased claim), a bitrate token
   `"{Bitrate} kbps"`-style label built from the raw `Bitrate` claim, `Protocol`
   (upper-cased), and a live/on-demand token from `IsLive` via localization
   (`LiveLabel` / `OnDemandLabel`). Skip any null/blank field. Prefer a
   localized bitrate label pattern (`BitrateValue` = `"{0} kbps"`); if the raw
   claim already contains a non-numeric unit, show it verbatim.
2. [ChannelRow.cs](../../src/StreamsPlayer.App/ChannelRow.cs): add
   `public Visibility TechnicalDetailsVisibility` ⇒ `Visible` when
   `TechnicalDetails` is non-empty, else `Collapsed`. Include both in the
   `RefreshLocalization`/`UpdateChannel` `OnPropertyChanged(string.Empty)` sweep
   (already fires for all properties).
3. [MainWindow.xaml](../../src/StreamsPlayer.App/MainWindow.xaml)
   `StreamCardTemplate`: add a 4th `RowDefinition` and a `TextBlock` bound to
   `TechnicalDetails`, `Visibility="{Binding TechnicalDetailsVisibility}"`,
   muted small font, `TextTrimming="CharacterEllipsis"`, tooltip = same. Keep it
   below the existing metadata line; do not crowd the default card (AC1) — the
   line is absent when no fields are present.
4. Grid tile: leave the tile visuals unchanged (details belong to the list card;
   the tile already shows kind). No change to `StreamGridTileTemplate`.
5. Localization: add keys to
   [Localization.en.xaml](../../src/StreamsPlayer.App/Localization.en.xaml) and
   [Localization.ru.xaml](../../src/StreamsPlayer.App/Localization.ru.xaml):
   `LiveLabel` (Live / Прямой эфир), `OnDemandLabel` (On demand / По запросу),
   `BitrateValue` (`{0} kbps` / `{0} кбит/с`).

## Notes

- No playback path, status bullet, or `LastPlayOutcome` is touched — AC5 held.
- Present-only rendering satisfies the "one quiet fallback" constraint: a card
  with no technical claims simply omits the line rather than showing an error.

## Static check

`dotnet build src/StreamsPlayer.App -c Debug`
expected: App builds; XAML compiles | actual: Build succeeded, 0 warnings, 0 errors
