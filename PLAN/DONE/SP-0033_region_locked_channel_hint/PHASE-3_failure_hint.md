# Phase 3 - Conditional region hint in the failure dialog

**Produces:** a region-restriction explanation shown only for a tagged channel that actually failed.
**Consumes:** `StreamChannel.Access` (Phase 1), localized strings (Phase 2).

## Steps

### 3.1 Localized string

`src/StreamsPlayer.App/Localization.en.xaml`, `.ru.xaml`, `.uk.xaml` - add `FailureRegionRestricted`
immediately after the existing `FailureDialogMessage` key. Hedged wording, no emoji.

- en: `The catalog marks this channel as region-locked: it did not respond from the maintainer's country. It may work if you are in the region it broadcasts to, and a proxy or VPN is not required for the rest of the catalog.`
- ru: `Каталог отмечает этот канал как доступный только в своём регионе: из страны составителя он не отвечал. Он может работать, если вы находитесь в регионе вещания.`
- uk: `Каталог позначає цей канал як доступний лише у своєму регіоні: із країни упорядника він не відповідав. Він може працювати, якщо ви перебуваєте в регіоні мовлення.`

Keep the English string's second clause short enough that the dialog does not become a wall of text;
Russian and Ukrainian omit it deliberately.

### 3.2 Dialog surface

`src/StreamsPlayer.App/PlaybackFailureDialog.xaml`:

- Insert a new `Auto` row between the message row and the `CopiedText` row.
- Add `<TextBlock x:Name="RegionRestrictedText" Grid.Row="1" TextWrapping="Wrap" Visibility="Collapsed"
  Margin="0,10,0,0" Foreground="{StaticResource MutedBrush}" Text="{DynamicResource FailureRegionRestricted}" />`
- Shift `CopiedText` to `Grid.Row="2"` and the button `StackPanel` to `Grid.Row="3"`, and add the
  matching fourth `RowDefinition`.
- Replace the fixed `Height="180"` with `SizeToContent="Height"` plus `MinHeight="180"` so the dialog
  grows for the hint instead of clipping it. `ResizeMode="NoResize"` stays.

### 3.3 Conditional display

`src/StreamsPlayer.App/PlaybackFailureDialog.xaml.cs` - add a `ChannelAccess access` parameter to the
`internal PlaybackFailureDialog(...)` constructor and set

```csharp
RegionRestrictedText.Visibility = access == ChannelAccess.GeoRestricted
    ? Visibility.Visible
    : Visibility.Collapsed;
```

The hint is therefore reachable only from the failure path, so a channel that plays shows nothing
(AC 5, Decision 5).

### 3.4 Call sites

Pass the channel's access value at both construction sites - no other call site exists:

- `src/StreamsPlayer.App/MainWindow.xaml.cs` (the `new PlaybackFailureDialog(channel.Title, channel.SourceOrigin, report)` call) - add `channel.Access`.
- `src/StreamsPlayer.App/PlayerWindow.xaml.cs` (the `new PlaybackFailureDialog(_channel.Title, _channel.SourceOrigin, report)` call) - add `_channel.Access`.

### 3.5 Report body unchanged

Do **not** add the access value to `FailureReportFormatter`. Its field list is a deliberate,
contract-bounded set (SP-0020) and widening it is out of scope for this ticket.

## Static check

`dotnet build StreamsPlayer.sln -c Release`

expected: 0 errors, 0 warnings; both call sites updated so no overload resolution error remains | actual:
Build succeeded, 0 Warning(s), 0 Error(s). The constructor parameter is required (no default), so the
compiler proved both call sites were updated - a third, missed site would not have built.

The English string dropped the plan's trailing proxy/VPN clause: it invited exactly the "use a VPN"
reading the strategic non-goals rule out. All three languages now carry the same two sentences.

**Status: complete.**
