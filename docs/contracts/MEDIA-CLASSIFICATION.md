# MEDIA-CLASSIFICATION - pointer

| | |
| --- | --- |
| **Id** | `MEDIA-CLASSIFICATION` |
| **Version** | 0.10 (draft) |
| **Home** | `Contracts/media-classification/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer** |

## What this repository must do to stay conformant

1. **Normalized Media Kind:** Classify streams and URLs into standard `Audio`, `Video`, and `Rtsp` kinds.
2. **Extension Classification:** Recognize standard video extensions (`.m3u8`, `.mpd`, `.mp4`, `.mkv`, `.webm`, `.ts`, `.mov`) and RTSP URI schemes (`rtsp://`), defaulting unknown or unparseable URLs to `Audio`.
3. **Catalog Header Agreement:** Map upstream catalog `media_kind` tokens (`AUDIO`, `VIDEO`, `RTSP`) consistently, falling back to URL classification for blank or unrecognised values.

Evidence: `src/StreamsPlayer.Core/StreamMediaKindClassifier.cs`, `Models.cs:5-10`. Tests: `StreamMediaKindClassifierTests.cs`.
