# Phase 05 — App lifecycle + UI wiring

**Produces:** `src/StreamsPlayer.App/MainWindow.NowPlaying.cs` (new partial) plus
small edits to `MainWindow.xaml.cs` and `MainWindow.Previews.cs`.
**Consumes:** Phase 02 (`IcyMetadataReader`), Phase 04 (`NowPlayingWithTrack`)

## Changes

### New partial `MainWindow.NowPlaying.cs`

Holds the ICY concern (keeps `MainWindow.xaml.cs` lean, mirrors the partial-by-
concern pattern):

- Fields: `private readonly HttpClient _icyHttpClient` (constructed with
  `Timeout = Timeout.InfiniteTimeSpan`, UserAgent `StreamsPlayer/0.1`);
  `private CancellationTokenSource? _icyCts;` `private int _nowPlayingGeneration;`
  — initialize `_icyHttpClient` in a small init method called from the ctor, or
  inline as a field initializer.
- `private void StartNowPlayingMetadata(StreamChannel channel)`:
  - Guard: only when `Uri.TryCreate(channel.Url, ...)` scheme is `http`/`https`
    (constraint: metadata only during an HTTP(S) audio attempt).
  - `var gen = ++_nowPlayingGeneration;`
  - `_icyCts = new CancellationTokenSource();`
  - Build `var progress = new Progress<string?>(title => OnNowPlayingTitle(gen, title));`
    (constructed on the UI thread → callback marshals back to it).
  - Fire-and-forget `_ = ReadIcyAsync(channel.Url, progress, _icyCts.Token);`
    where `ReadIcyAsync` wraps `new IcyMetadataReader(_icyHttpClient).ReadAsync(...)`
    in a catch-all (belt-and-braces; the reader already never throws).
- `private void StopNowPlayingMetadata()`:
  `_nowPlayingGeneration++;` (invalidate any in-flight report),
  `_icyCts?.Cancel(); _icyCts?.Dispose(); _icyCts = null;`
- `private void OnNowPlayingTitle(int generation, string? title)`:
  - Return if `generation != _nowPlayingGeneration` (stale reader) or
    `_playingAudio is null` (playback already gone) — AC #3/#4.
  - If `string.IsNullOrWhiteSpace(title)` → `SetNowPlaying("NowPlaying", _playingAudio.DisplayTitle)`.
  - Else → `SetNowPlaying("NowPlayingWithTrack", _playingAudio.DisplayTitle, title)`.

### `MainWindow.xaml.cs`

- In `PlayChannelAsync`, audio branch, after `AudioPlayer.Play();`
  (after [line 621](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L621)):
  call `StartNowPlayingMetadata(channel);`
- In `StopAudioPlayback()` ([line 708](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L708)):
  call `StopNowPlayingMetadata();` (before or after clearing `_playingAudio`;
  the generation bump makes ordering safe). This single site covers stop,
  station-switch (called at line 610), terminal failure (via `StopAudio`), and
  window-hide.

### `MainWindow.Previews.cs`

- In `MainWindow_Closed` ([line 205](../../src/StreamsPlayer.App/MainWindow.Previews.cs#L205)),
  alongside `_httpClient.Dispose();` add `StopNowPlayingMetadata();` and
  `_icyHttpClient.Dispose();`

## Constraints honored

- No `CatalogState`/store write → nothing persisted (AC #5).
- No `CurrentLog` noise required; keep it minimal (an `AUDIO` event already logs
  start). Do not add per-title logging (session-only, untrusted).
- Fire-and-forget is isolated by a catch-all and torn down by cancellation on
  every playback exit → no lifecycle-unsafe leak.

## Static check

`dotnet build StreamsPlayer.sln -c Debug`
expected: solution builds, 0 new warnings | actual: Build succeeded, 0 Warning(s), 0 Error(s). (New partial needed an explicit `using System.Net.Http;` — the WindowsDesktop SDK omits it from implicit usings.)
