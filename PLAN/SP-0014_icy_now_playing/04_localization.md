# Phase 04 — Localized now-playing-with-track string

**Produces:** `NowPlayingWithTrack` key in both localization dictionaries
**Consumes:** — (pairs with the existing `NowPlaying` key at line 96)

## Change

Add one parallel key to both files, immediately after the existing `NowPlaying`
entry so the two files stay line-aligned:

- `src/StreamsPlayer.App/Localization.en.xaml`:
  `<sys:String x:Key="NowPlayingWithTrack">Now playing: {0} — {1}</sys:String>`
- `src/StreamsPlayer.App/Localization.ru.xaml`:
  `<sys:String x:Key="NowPlayingWithTrack">Сейчас играет: {0} — {1}</sys:String>`

`{0}` = station identity (`DisplayTitle`), `{1}` = untrusted ICY track text.
Station-only presentation continues to use the existing `NowPlaying` = `"{0}"`
key, so no metadata → no visible change.

## Rationale

Reusing `SetNowPlaying(key, args)` (which persists key + args) means a mid-track
language toggle re-renders correctly via `RefreshLocalizedStateText()`. Both
dictionaries must carry the key or a `{DynamicResource}`/`Format` lookup falls
back to the key name.

## Static check

`dotnet build src/StreamsPlayer.App -c Debug`
expected: XAML compiles, key present in both dictionaries | actual: Solution builds; `NowPlayingWithTrack` present in en (`= "Now playing: {0} — {1}"`) and ru (`= "Сейчас играет: {0} — {1}"`).
