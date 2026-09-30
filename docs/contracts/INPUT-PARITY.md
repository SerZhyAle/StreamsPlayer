# INPUT-PARITY - pointer

| | |
| --- | --- |
| **Id** | `INPUT-PARITY` (with `INPUT-CHORD` - not adopted) |
| **Version** | 0.3 (draft) |
| **Home** | `Contracts/input-controls/README.md` |
| **Owner** | shared (column stewards) |
| **This product's role** | **consumer** (keyboard, mouse, media keys) |

## What this repository must do to stay conformant

1. **Keyboard Accessibility:** Core playback and application controls are reachable via keyboard (`Space` for play/pause, `Left`/`Right` for seek in player window, `PageUp`/`PageDown`/`Home`/`End` for list paging, `Ctrl+F` for filter/search, `Esc` for cancel/close, `Enter` for confirmation, `Shift+F10` or `Menu` key for context menu, `Alt+Left` or `Backspace` for return).
2. **Media Keys Integration:** Windows System Media Transport Controls (`MainWindow.SystemMedia.cs`) maps media keys (`Play`, `Pause`, `Stop`, `PreviousTrack`, `NextTrack`) to live audio stream playback when enabled in Settings.
3. **Device Parity:** No playback, playlist, or settings action is locked behind pointer-only interaction.
4. **INPUT-CHORD Scope:** `INPUT-CHORD` is not adopted; StreamsPlayer neither captures nor stores system-wide chord combinations.

Evidence: `src/StreamsPlayer.App/MainWindow.xaml.cs`, `MainWindow.SystemMedia.cs`, `PlayerWindow.xaml.cs`, `tests/StreamsPlayer.Core.Tests/DesktopUxConformanceTests.cs`.
