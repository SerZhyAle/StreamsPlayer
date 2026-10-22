# INPUT-PARITY - pointer

| | |
| --- | --- |
| **Id** | `INPUT-PARITY` |
| **Version** | 0.4, 2026-10-22 (SP-0232) |
| **Home** | `Contracts/input-controls/README.md` |
| **Owner** | Shared, with column stewards |
| **This product's role** | Consumer: Windows live playback, keyboard/mouse/SMTC |

`MainWindow.SystemMedia` maps media transport events. `PlayerWindow.Window_KeyDown` currently
provides F11 fullscreen, Escape and R recording. No system-wide chord capture/storage exists.
The earlier pointer's claim of Left/Right seeking and a verified back/default set was not supported
by that handler and is withdrawn. File-only actions do not arise in this live-stream product;
applicable shared defaults and editor/modifier isolation remain partial, assigned to SP-0239.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
