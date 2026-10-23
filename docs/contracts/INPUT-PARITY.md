# INPUT-PARITY - pointer

| | |
| --- | --- |
| **Id** | `INPUT-PARITY` |
| **Version** | 0.4, 2026-10-22 (SP-0232) |
| **Home** | `Contracts/input-controls/README.md` |
| **Owner** | Shared, with column stewards |
| **This product's role** | Consumer: Windows live playback, keyboard/mouse/SMTC |

`MainWindow.SystemMedia` maps media transport events. `PlayerKeys.Resolve` (Core) decides and
`PlayerWindow.Keyboard` performs the live player's keys: `F`/`F11` fullscreen, `M` mute,
`Ctrl+Up`/`Ctrl+Down` volume, `A` audio track, `R` recording, `Esc` leaving fullscreen. A key never
acts while a field, an open menu or an earlier handler owns it, and a modifier or an auto-repeat
disqualifies the bare keys. `R` stays recording where the contract says rotate: a live engine has no
rotation and the key is user-documented; a recorded exception, not a rename. No system-wide chord
capture/storage exists, so there are no user mappings to migrate. File-only actions (file movement,
seek, speed, frame step, zoom, rotation, slideshow, destinations) do not arise in this live-stream
product. Unit tests cover the decision; keyboard focus on the controls overlay and both engines still
need a run-and-observe pass (SP-0239). The earlier pointer's claim of Left/Right seeking is withdrawn.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
