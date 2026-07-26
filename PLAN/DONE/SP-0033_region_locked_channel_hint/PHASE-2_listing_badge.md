# Phase 2 - Unobtrusive marker in list and grid

**Produces:** `ChannelRow` region-restricted presentation members, list + grid markers, localized strings.
**Consumes:** `StreamChannel.Access` from Phase 1.

## Steps

### 2.1 Localized strings

`src/StreamsPlayer.App/Localization.en.xaml`, `.ru.xaml`, `.uk.xaml` - add two keys next to the
existing `LiveLabel`/`OnDemandLabel` technical-claim strings. No emoji.

- `RegionRestrictedLabel` - the short badge text.
  - en: `Region-locked`
  - ru: `Только для региона`
  - uk: `Лише для регіону`
- `RegionRestrictedTip` - the hedged tooltip (Decision 3 - *may*, never *is*).
  - en: `The catalog maintainer could not reach this channel from their country. It may still work in the region it is broadcast to.`
  - ru: `Составитель каталога не смог открыть этот канал из своей страны. В регионе вещания он всё же может работать.`
  - uk: `Упорядник каталогу не зміг відкрити цей канал зі своєї країни. У регіоні мовлення він усе ж може працювати.`

Keys must be added to all three files in the same relative position.

### 2.2 View-model members

`src/StreamsPlayer.App/ChannelRow.cs` - add, next to `PinnedVisibility`:

```csharp
public Visibility RegionRestrictedVisibility =>
    Channel.Access == ChannelAccess.GeoRestricted ? Visibility.Visible : Visibility.Collapsed;
public string RegionRestrictedLabel => LocalizationService.Get("RegionRestrictedLabel");
public string RegionRestrictedTip => LocalizationService.Get("RegionRestrictedTip");
```

Do **not** fold the marker into `Metadata` or `TechnicalDetails`: those are neutral maintainer claims,
and a region warning must be visually distinct rather than another dot-separated fragment.

`RefreshLocalization` already raises `PropertyChanged` for all properties, so a language switch
re-reads the new strings with no further change.

### 2.3 List marker

`src/StreamsPlayer.App/MainWindow.xaml`, `StreamListItemTemplate` - add a pill `Border` on the
metadata row (`Grid.Row="2"`, the `Metadata` line), placed **after** the existing metadata text so it
never displaces the title, favicon, status ellipse, or pin button (Decision 4).

Wrap the existing `Grid.Row="2"` `TextBlock` and the new pill in a horizontal `StackPanel` occupying
that row. Pill: `CornerRadius="9"`, `Padding="7,2"`, `Background="#22B36B00"`,
`Foreground="#FF8A5300"`, `FontSize="10"`, text bound to `RegionRestrictedLabel`, with
`Visibility="{Binding RegionRestrictedVisibility}"`, `ToolTip="{Binding RegionRestrictedTip}"`, and
`AutomationProperties.Name="{Binding RegionRestrictedLabel}"`.

Amber, not red: red is already `StatusBrush`'s failure colour and this is not a failure.

### 2.4 Grid marker

`src/StreamsPlayer.App/MainWindow.xaml`, `StreamGridTileTemplate` - add the same pill,
`HorizontalAlignment="Left" VerticalAlignment="Top"`, stacked directly **below** the existing pinned
badge so the two never overlap when a channel is both pinned and region-locked. Keep the existing
`Margin="9"` pinned badge untouched and give the new pill a top margin that clears it.

The tile content sits inside a `Viewbox` with `Stretch="Uniform"`, so the pill scales with the tile
and stays legible at Small, Medium, and Large without per-size values (AC 2).

### 2.5 Untagged rows unchanged

No template change may alter layout when `RegionRestrictedVisibility` is `Collapsed`. The pill must
be inside a container that collapses to zero width/height, not a fixed-size cell (AC 3).

## Static check

`dotnet build StreamsPlayer.sln -c Release`

expected: 0 errors, 0 warnings; XAML compiles with the new bindings resolved | actual: Build succeeded,
0 Warning(s), 0 Error(s).

**Deviation from 2.4 (better):** rather than giving the grid pill a hardcoded top margin that clears the
pinned badge, both badges now share one top-left `StackPanel`. An unpinned region-locked channel rises
into the top slot instead of leaving a gap, and the no-overlap guarantee is structural rather than a
magic number. Pill uses solid `#D9B36B00` on the grid (matching the pinned badge's opacity treatment
over artwork) and the lighter `#22B36B00` wash on the list.

**Status: complete.**
