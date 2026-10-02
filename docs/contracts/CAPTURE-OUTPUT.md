# CAPTURE-OUTPUT - pointer

| | |
| --- | --- |
| **Id** | `CAPTURE-OUTPUT` |
| **Version** | 0.3 (draft - being drawn; a product supplements it by proposal) |
| **Home** | `Contracts/capture-output/README.md` |
| **Owner** | FastMediaSorter Android - amended by a `PROPOSAL-*` beside the contract, never by edit |
| **This product's role** | **producer** of `video_frame`, `stream_video` and `stream_audio`; it reads none |

## What this product writes

- a **video frame** saved from the picture - `video_frame`, JPEG, role `frames` (`Pictures\Frames`);
- a **recording of a live video stream** - `stream_video`, the source's own container (`.mp4`, `.ts`), role
  `stream-recordings` (`Videos\Recordings`);
- a **recording of a radio station** - `stream_audio`, the station's own bytes, extension from what it sent,
  role `stream-recordings` (`Music\Recordings`).

The two stream kinds, the channel label in the name and the rule 12 reading for a recording that grows came
from this product's proposal and are in the contract since 0.2 (SP-0179).

Out of the contract's scope by its section 5 and not read against it: the M3U playlist export
(`USER-PLAYLIST`), the log archive (`DIAGNOSTIC-REPORT` - it goes to the frames folder when one is chosen,
else Downloads), the channel icon and the grid previews (private state), the Store screenshots (a render
target).

## What this repository must do to stay conformant

The rules are in the home page. Repo-local: which code holds each one, so a change can be routed.

| Rule | Held by |
| --- | --- |
| 1, 3, 4 kind, name, label, extension | `CaptureFileName` (Core) - prefix, invariant `yyMMdd_HHmmss` of the capture's start, rule 7-cleaned label cut to 80, lowercase extension |
| 5, 6 never reused, never overwritten | `CaptureFileName.FirstFree` / `WithOrdinal`, used by `CaptureFolders.ReserveUniquePath` against the destination folder; moves and creates never overwrite |
| 9 default folder per role | `CaptureFolders.Default` (known folders by `SHGetKnownFolderPath`) |
| 10 per-kind choice | `CatalogState.FrameFolder`, `VideoRecordingFolder`, `AudioRecordingFolder` (Settings, Playback tab); `CaptureFolderChoices.Split` carried the old single choice over once |
| 11 fallback, told | `CaptureFolderChain` (chosen, default, Downloads) walked by `CapturedFrameWriter`, `StreamAudioRecorder`, `RecordingFinisher` and the recording start; the notices `FrameSavedElsewhere`, `RecordFellBack`, `RecordSavedElsewhere` name both folders |
| 12 visible when complete | a frame is written as `~<name>.partial` and renamed; recordings grow in place (allowed since 0.2) or, on LibVLC, stage privately and move; leftovers are handed over at the next start |
| 16 the capture time inside | the frame's EXIF `DateTimeOriginal`; the recordings' container time is the engine's |

**Conformance evidence.** The contract has no vectors yet. Rung 1 of its ladder runs in `dotnet test`:
`CaptureFileNameTests` (every kind at a fixed instant in ar-SA, fa-IR, hi-IN and th-TH, the ordinal
included), `CaptureFolderChainTests`, `CaptureFolderChoicesTests`.

**Deviations** are recorded in the store's `_meta/REGISTRY.md`, never here.
