# PHASE-3 — FlyleafLib backend implementation

**Produces:** `FlyleafLib` package reference, `FlyleafVideoBackend : IVideoBackend`, factory arm
selecting Flyleaf when persisted.
**Consumes:** Phase 2 (`IVideoBackend`, factory).
**Goal (AC 2, AC 4, Decision 5):** a working experimental second engine that plays HLS-live and
RTSP through the exact same seam; parity gaps are surfaced by the experimental label (Phase 4), not
by a crash or silent freeze.

## Step 0 — pin and verify the API surface (stop-on-ambiguity gate)

The exact FlyleafLib 3.10.x API is **not** assumed by this plan. Before wiring, verify the current
surface against the installed package and its docs (`Player`, its host control, `Player.Open(url)`,
volume/mute, status/buffering events, error/end events, snapshot-to-file or frame grab, audio/video
stream lists). Sources: FlyleafLib NuGet page and `SuRGeoNix/Flyleaf` wiki (Config, Player,
FlyleafHost). If an interface method has no Flyleaf equivalent (e.g. per-track SPU selection),
implement it as a documented no-op / empty list — that is an allowed experimental gap, not a
blocker. Record the resolved API mapping in this phase file before step 2.

## Steps

1. **Add the package.** In
   [src/StreamsPlayer.App/StreamsPlayer.App.csproj](../../src/StreamsPlayer.App/StreamsPlayer.App.csproj)
   add `<PackageReference Include="FlyleafLib" Version="3.10.4" />` (or the latest verified 3.10.x)
   plus its FFmpeg native-libs package for `win-x64`/`win-arm64` as the package's install docs
   require. Confirm `dotnet restore` resolves for both RIDs the app builds.

2. **Implement `FlyleafVideoBackend`.** Create `src/StreamsPlayer.App/FlyleafVideoBackend.cs`
   implementing `IVideoBackend`, mapping each member to the verified Flyleaf API:
   - `View` = the Flyleaf WPF host control bound to a `Player`;
   - engine `Config` set for the resilience baseline where Flyleaf exposes it (tuning doc §):
     **software-decode tolerance** (disable/limit HW decode), **RTSP-over-TCP**
     (`rtsp_transport=tcp` via FFmpeg format options), a **single live buffer** sized to the
     `cacheMilliseconds` passed to `Play`, and **rebuffer-in-place** (no reconnect-to-grow). Any
     baseline behaviour Flyleaf cannot reproduce is listed here as a known gap for AC 4 evidence;
   - `Play` opens the URL with those options and the reconnect vs initial cache size;
   - `PositionMs` / `IsPlaying` from the player's current time / status so the **existing window
     watchdog and recovery policy drive Flyleaf unchanged**;
   - `Volume` / `Mute` map to the player;
   - map buffering/status → `BufferingChanged` (0..100) + `ReachedPlaying`; open → `Opening`;
     end-of-stream → `EndReached`; error/failure → `EncounteredError`; stream/track list change →
     `TracksChanged`;
   - `RequestSnapshot` → Flyleaf frame/snapshot to a `BitmapSource`, raise `SnapshotReady`
     (feeds the same grid-thumbnail + save-frame toast path). If unavailable, return `false`
     (documented gap; save-frame simply no-ops as it already does before first frame);
   - `AudioTracks` / `SubtitleTracks` / selection from Flyleaf's stream lists, or empty +
     no-op where not offered (the window auto-hides the track buttons when a list has ≤1 entry);
   - `LogStats` = no-op (documented — Flyleaf has no equivalent counter set);
   - `StopAndDisposeAsync` disposes the player/host off the UI thread, matching the LibVLC teardown
     contract (no UI-thread block on a flapping stream).

3. **Enable the factory arm.** In
   [VideoBackendFactory.cs](../../src/StreamsPlayer.App/VideoBackendFactory.cs) uncomment/add
   `MediaBackend.Flyleaf => new FlyleafVideoBackend(volume, muted, log)`.

## Static check

- `dotnet build StreamsPlayer.sln -c Release` → **expected:** restore resolves FlyleafLib +
  FFmpeg natives; build succeeds | **actual:** _record._
- **Run-and-observe (Flyleaf plays):** temporarily force the factory to Flyleaf (or complete Phase 4
  first), open **one HLS-live** and **one RTSP** stream; confirm each renders video+audio. Then open
  a stream known troublesome on LibVLC. Capture from `Current.log` / observation which resilience
  behaviours reproduce and which do not. **expected:** both stream families play; no crash or silent
  freeze; gaps enumerated | **actual:** _record._
