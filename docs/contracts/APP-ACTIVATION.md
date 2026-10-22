# APP-ACTIVATION - pointer

| | |
| --- | --- |
| **Id** | `APP-ACTIVATION` |
| **Version** | 0.13, 2026-10-22 (SP-0232) |
| **Home** | `Contracts/app-activation/README.md` |
| **Owner** | CyrFlip |
| **This product's role** | Consumer: CLI plus acknowledged, current-user pipe; no URI scheme |

`SingleInstanceIdentity`, `ActivationMessage`, `ActivationPipe`, `ForegroundActivation` and
`MainWindow.Launch` hold the boundary. Frozen mutex/pipe/profile names remain unchanged.
The earlier implementation claim stays at 0.11; source/pipe tests do not prove another-user/session,
hung-window or second-start foreground acceptance (SP-0239). Frozen-name policy supersedes the old
names-only exception; it does not certify a runtime test that was not performed.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
