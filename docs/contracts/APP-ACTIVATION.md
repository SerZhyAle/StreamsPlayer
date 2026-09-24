# APP-ACTIVATION - pointer

| | |
| --- | --- |
| **Id** | `APP-ACTIVATION` |
| **Version** | 0.9 (draft) |
| **Home** | `Contracts/app-activation/README.md` |
| **Owner** | CyrFlip |
| **This product's role** | **consumer** |

## What this repository must do to stay conformant

1. **Launch Arguments & Target Resolution:** Parse command-line invocations and deep-link arguments cleanly into `StreamLaunchRequest` (`Url`, `ChannelId`, `None`, `Invalid`).
2. **Safe Degradation:** Unrecognized or invalid launch parameters report friendly status in UI without crashing the application.
3. **Focus Restoration:** Bring window to front when activated with explicit playback targets.

Evidence: `src/StreamsPlayer.Core/Models.cs:95-135`, `src/StreamsPlayer.App/MainWindow.Launch.cs`, `App.xaml.cs:21`. Tests: `StreamLaunchRequestTests.cs`.
