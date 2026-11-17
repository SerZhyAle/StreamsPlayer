# APP-COMMANDS - pointer

| | |
| --- | --- |
| **Id** | `APP-COMMANDS` |
| **Version** | 0.2 (draft), read 2026-11-15 (SP-0278); built against 0.1 (SP-0276) |
| **Home** | `Contracts/app-commands/README.md` |
| **Owner** | shared |
| **This product's role** | Consumer: the request-response profile over the `APP-ACTIVATION` pipe, the five base commands and the playback, record and channel commands, all declared in one manifest |

`AppCommandProtocol`, `AppCommandLine`, `AppCommandPolicy`, `AppCommandClient`, `AppCommandCaller` and `AppCommandConsole`
in Core hold the boundary; `AppPlaybackCommands`, `AppRecordCommands` and `AppChannelCommands` hold the verbs;
`src/StreamsPlayer.Cli` is the console companion. The manifest of record (rule 18) is
[`../cli-commands.json`](../cli-commands.json); [`../CLI.md`](../CLI.md) is generated from it by
`tools/Build-CliDoc.ps1` (rule 20) and is never edited by hand. `scripts/check-fast.ps1` fails when the two disagree or an
entry lacks a member of rule 18.

The conformance cases C1 to C13, the tests that hold each and the cases that stay manual are named in this product's row of
the contract store's registry, which is the conformance authority; a read version does not upgrade an implementation claim.
