# CAPTURE-OUTPUT - pointer

| | |
| --- | --- |
| **Id** | `CAPTURE-OUTPUT` |
| **Version** | 0.6, 2026-10-22 (SP-0232) |
| **Home** | `Contracts/capture-output/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | Producer of video_frame, stream_video and stream_audio |

`CaptureFileName`, `CaptureFolders`, `CapturedFrameWriter`, `StreamAudioRecorder` and
`RecordingFinisher` hold naming, collision, folder and completion behavior. Frames are JPEG;
recordings retain their source format. This product writes no PNG or plain-text capture, so the
2026-10-17 text-time clarification adds no writer. Earlier implementation 0.5 is retained; EXIF/name
and both engines' container-time equality need fresh runtime measurement in SP-0239.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
