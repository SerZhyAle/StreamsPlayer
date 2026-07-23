# PHASE-2 — App video-backend seam + LibVLC extraction

**Produces:** `IVideoBackend` + supporting types, `LibVlcVideoBackend` (verbatim move of today's
LibVLC code), `PlayerWindow` refactored to consume the seam, a backend factory that (for this phase)
always returns LibVLC.
**Consumes:** Phase 1 (`MediaBackend`).
**Goal (AC 3, AC 6, risk mitigation):** introduce the seam with **no behaviour change** — the
default LibVLC path must remain byte-for-byte equivalent (fullscreen, volume/mute, tracks,
thumbnails, recovery, watchdog, teardown threading all preserved). This is the riskiest phase; it
is behaviour-preserving refactor only, no Flyleaf yet.

## Seam contract

New file `src/StreamsPlayer.App/IVideoBackend.cs` — App-internal, no Core change:

```csharp
internal interface IVideoBackend : IAsyncDisposable
{
    // The WPF element PlayerWindow hosts (LibVLC: VideoView; Flyleaf: its host control).
    FrameworkElement View { get; }

    // Live media position / state the watchdog reads (engine-agnostic units: ms, playing?).
    long PositionMs { get; }
    bool IsPlaying { get; }

    int Volume { set; }
    bool Mute { set; }

    void Play(Uri url, uint cacheMilliseconds, bool rtspOverTcp, bool softwareDecode);
    Task StopAndDisposeAsync();          // off-UI-thread teardown (today's Task.Run + _mediaGate)

    bool RequestSnapshot(int width);     // async; result arrives via SnapshotReady
    IReadOnlyList<VideoTrack> AudioTracks { get; }
    IReadOnlyList<VideoTrack> SubtitleTracks { get; }
    int SelectedAudioTrackId { get; }
    int SelectedSubtitleTrackId { get; }
    void SelectAudioTrack(int id);
    void SelectSubtitleTrack(int id);

    event Action<float> BufferingChanged;      // cache % 0..100
    event Action ReachedPlaying;               // maps LibVLC Playing
    event Action Opening;
    event Action Stopped;
    event Action EndReached;
    event Action EncounteredError;
    event Action TracksChanged;
    event Action<BitmapSource> SnapshotReady;
    event Action<string> DiagnosticLog;        // warn/error lines for CurrentLog (VLC log today)
}

internal readonly record struct VideoTrack(int Id, string? Name);
```

`readonly record struct VideoTrack` replaces direct use of `LibVLCSharp` `TrackDescription` in the
window so the track menu (`OpenTrackMenu`) becomes engine-agnostic.

Live statistics (`LogStats`) are LibVLC-specific telemetry. Keep them **inside**
`LibVlcVideoBackend`: add an optional `void LogStats(string tag)` on the interface with a no-op
default in Flyleaf. The window calls `_backend.LogStats("STATS")` from its existing timer; LibVLC
logs the full counter set, Flyleaf logs nothing (documented experimental gap).

## Steps

1. **Add the seam types.** Create `IVideoBackend.cs` and `VideoTrack` as above. Add `LogStats`
   to the interface.

2. **Extract `LibVlcVideoBackend`.** Create `src/StreamsPlayer.App/LibVlcVideoBackend.cs`
   implementing `IVideoBackend`. Move verbatim from
   [PlayerWindow.xaml.cs](../../src/StreamsPlayer.App/PlayerWindow.xaml.cs):
   - construction of `LibVLC` (line 93–99: `Core.Initialize()`, the exact option string
     `--no-video-title-show --no-osd --no-snapshot-preview --rtsp-tcp --clock-jitter=0
     --avcodec-hw=none`, `MediaPlayer` with `NetworkCaching`);
   - `View` = a `LibVLCSharp.WPF.VideoView` created in code with `MediaPlayer` assigned;
   - `Play` = today's `StartMedia` body (new `Media`, `:network-caching`/`:live-caching`/`:rtsp-tcp`/
     `:avcodec-hw=none` options, `_mediaPlayer.Play`, previous-media dispose, `_mediaGate` lock).
     The `PLAYBACK OPEN` log line stays in the window (it has the `reason`/`kind`); the backend
     takes the resolved `cacheMilliseconds`/flags;
   - snapshot: `CaptureThumbnail` + `MediaPlayer_SnapshotTaken` + `LoadFrozenImage` + `TryDeleteFile`
     move in; `RequestSnapshot` returns the `TakeSnapshot` bool and raises `SnapshotReady` with the
     decoded `BitmapSource`;
   - track APIs map `AudioTrackDescription`/`SpuDescription`/`AudioTrack`/`Spu`/`SetAudioTrack`/
     `SetSpu` to the `VideoTrack` shape;
   - all `_mediaPlayer.*` event handlers become raised interface events; `LibVlc_Log` →
     `DiagnosticLog`; `LogStats` counter block moves in;
   - `StopAndDisposeAsync` = today's `PlayerWindow_Closed` teardown `Task.Run` block (Stop, media
     dispose, player dispose, libVlc dispose) behind the same `_mediaGate`.
   Keep the `_mediaGate` and `_closing` semantics **inside** the backend so the Play/Dispose race
   protection is preserved exactly.

3. **Refactor `PlayerWindow` to the seam.** In
   [PlayerWindow.xaml.cs](../../src/StreamsPlayer.App/PlayerWindow.xaml.cs):
   - remove the `using LibVLCSharp.*` lines and the `_libVlc` / `_mediaPlayer` / `_media` /
     `_mediaGate` fields; add `private readonly IVideoBackend _backend;`
   - constructor gains a `MediaBackend backend` parameter (last, before `startFullscreen`) and
     builds `_backend = VideoBackendFactory.Create(backend, ...)`; subscribe to the interface events
     mapping 1:1 to the existing handlers (`BufferingChanged`→`UpdateBuffering`, `EndReached`→
     recovery, `EncounteredError`→recovery, `TracksChanged`→`RefreshTrackControls`, `SnapshotReady`→
     the `_onThumbnail`/toast hand-off, `DiagnosticLog`→`_log.Event("VLC"…)`);
   - insert `_backend.View` into a host container in XAML (see step 4);
   - `StartMedia` keeps its reason/cache-selection/`PLAYBACK OPEN` log, then calls
     `_backend.Play(new Uri(_channel.Url), cacheMs, rtspOverTcp:true, softwareDecode:true)`;
   - watchdog reads `_backend.PositionMs` / `_backend.IsPlaying` instead of `_mediaPlayer.Time` /
     `.State == VLCState.Playing`;
   - volume/mute setters call `_backend.Volume` / `_backend.Mute`;
   - `OpenTrackMenu` takes `IReadOnlyList<VideoTrack>` + selected id + `Action<int>`;
   - `PlayerWindow_Closed` awaits/launches `_backend.StopAndDisposeAsync()` (fire-and-forget
     `Task.Run` as today) after detaching events.

4. **XAML host swap.** In [PlayerWindow.xaml](../../src/StreamsPlayer.App/PlayerWindow.xaml)
   replace the `xmlns:vlc` `<vlc:VideoView x:Name="Player">` root with an engine-neutral host —
   e.g. a `<Grid x:Name="VideoHost">` whose first child the code sets to `_backend.View`, keeping
   the existing overlay `Grid` (control panel + toast) as the second child on top. The overlay
   markup and all `x:Name`s stay unchanged so no other handler moves.

5. **Backend factory.** Create `src/StreamsPlayer.App/VideoBackendFactory.cs`:
   ```csharp
   internal static class VideoBackendFactory
   {
       public static IVideoBackend Create(MediaBackend backend, int volume, bool muted, CurrentLog log)
           => backend switch
           {
               // MediaBackend.Flyleaf => new FlyleafVideoBackend(...),  // Phase 3
               _ => new LibVlcVideoBackend(volume, muted, log)
           };
   }
   ```
   For this phase the `Flyleaf` arm is absent/commented; every value yields LibVLC so behaviour is
   unchanged regardless of the (not-yet-wired) setting.

6. **Caller compiles.** [MainWindow.xaml.cs:838](../../src/StreamsPlayer.App/MainWindow.xaml.cs#L838)
   `new PlayerWindow(...)` gains one argument. For this phase pass the literal
   `MediaBackend.LibVlc` (real wiring in Phase 4) so the default path is provably unchanged.

## Static check

- `dotnet build StreamsPlayer.sln -c Release` → **expected:** succeeds, no `LibVLCSharp` symbol
  remains referenced in `PlayerWindow.xaml.cs` (moved into the backend) | **actual:** _record._
- `rg "LibVLCSharp|_mediaPlayer|_libVlc" src/StreamsPlayer.App/PlayerWindow.xaml.cs` → **expected:**
  no matches | **actual:** _record._
- **Run-and-observe (LibVLC unchanged):** launch app, open a video and an RTSP stream; confirm
  playback, buffering→live label, fullscreen (F11/Esc), volume + mute, track menus when offered,
  save-frame toast + grid thumbnail, and a forced-failure recovery all behave as before. Record
  `expected: identical to pre-refactor | actual: …` per item.
