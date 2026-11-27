# REMOTE-CONTROL - pointer

| | |
| --- | --- |
| **Id** | `REMOTE-CONTROL` |
| **Version** | 0.7 (draft), adoption in progress (STRWIN-0307, 2026-11-23) |
| **Home** | `Contracts/remote-control/README.md`; rule 18 is detailed in WINDOW.md. The carrier is `DEVICE-EXCHANGE` 1.18 items GC to GF (`Contracts/device-exchange/REMOTE-COMMAND-2026-11-20.md`) |
| **Owner** | shared |
| **This product's role** | Target: answers a sealed `command-request` from another device of the account with a sealed `command-result`; never a controller |

`RemoteCommandPayload`, `RemoteControlSettings` (with `RemoteControlPolicy`), `RemoteCommandRing`, `RemoteCommandLog`,
`RemoteControlStore`, `AppCommandRemote` and `ExchangeCommandFrames` in Core hold the boundary; `RemoteCommandTarget`
and `ExchangeSourceService.Commands.cs` in the App run it, and the External control
window carries the switch, grants, confirmations and log of rules 8 to 11 and 18. Its Settings entry is on the
Exchange server page. The `remote` member of rule 7 is
declared in the manifest of record, [`../cli-commands.json`](../cli-commands.json), and rendered into
[`../CLI.md`](../CLI.md).

The R and W cases, the tests that hold them and the cases that stay manual are named in this product's row of the
contract store's registry, which is the conformance authority; a read version does not upgrade an implementation claim.
