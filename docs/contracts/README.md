# docs/contracts/

Pointers, never copies. Every file here names one shared contract: its id, its version, its home in the
cross-project contract store, this product's role, and what this repository must do to stay conformant.

The contract store's location is named in exactly one tracked file - `CLAUDE.md`, first line. Everything
here cites a contract by its path under `Contracts/`, and source comments cite it by document and item -
`10_contract_amendment_2026-08-20.md item D`, `STREAM-BANK rule 5` - because whoever clones this
repository has no such drive.

A pointer that grows a second page has become a copy. If this repository believes a contract is wrong, the
amendment is written in the store, not here.

| File | Contract ID | Function | Role |
| --- | --- | --- | --- |
| [APP-BEHAVIOUR.md](APP-BEHAVIOUR.md) | `APP-BEHAVIOUR` | Desktop Application UX Behavior (12 shared moments) | **Owner**, P/C |
| [APP-SETTINGS.md](APP-SETTINGS.md) | `APP-SETTINGS` | Desktop Application Settings Surface Anatomy & Layout | Consumer (adopted 2026-10-02, SP-0188; read at 0.3, SP-0206) |
| [APP-STYLE.md](APP-STYLE.md) | `APP-STYLE` | Desktop Palette Vocabulary and Dynamic Theming | **Owner**, P/C |
| [WINDOWS-UI.md](WINDOWS-UI.md) | `WINDOWS-UI` | Shared Windows settings, dialogs and working-viewer profile | Consumer (adopted 2026-10-05, SP-0191; read at 0.3, SP-0206) |
| [USER-PLAYLIST.md](USER-PLAYLIST.md) | `USER-PLAYLIST` | User Stream Playlists & Extended M3U8 Interchange | **Owner**, P/C |
| [DIAGNOSTIC-REPORT.md](DIAGNOSTIC-REPORT.md) | `DIAGNOSTIC-REPORT` | Diagnostic Bundle, Environment Summary & Redaction | **Owner**, P/C |
| [STREAM-BANK.md](STREAM-BANK.md) | `STREAM-BANK` | Published Stream Catalog ZIP & Artwork Atlas | Consumer |
| [LIVE-BROADCAST.md](LIVE-BROADCAST.md) | `LIVE-BROADCAST` | Live Stream Descriptor & Direct Response Playback | Consumer (read at 0.18 on 2026-10-07, SP-0206; the additions are `[PROPOSED]` and owed by SP-0201, SP-0203 to SP-0205; item I for RTSP is implemented but unmeasured, SP-0208) |
| [DEVICE-EXCHANGE.md](DEVICE-EXCHANGE.md) | `DEVICE-EXCHANGE` | Device Exchange: a user's devices finding broadcasts through a server account or Google Drive | Consumer, receiver only (read 2026-10-07 at 0.17; not implemented, SP-0200 to SP-0205) |
| [WAVE-PARTICLES.md](WAVE-PARTICLES.md) | `WAVE-PARTICLES` | "Particles and Lines" Animated Backdrop Simulation | Consumer |
| [MEDIA-CLASSIFICATION.md](MEDIA-CLASSIFICATION.md) | `MEDIA-CLASSIFICATION` | Media Kind Resolution and Stream Classification | Consumer |
| [UPDATE-MANIFEST.md](UPDATE-MANIFEST.md) | `UPDATE-MANIFEST` | Non-Store App Update Feed & Integrity Check | Declared, not bound - no update client exists (SP-0206) |
| [LAN-DISCOVERY.md](LAN-DISCOVERY.md) | `LAN-DISCOVERY` | Local Network Service Discovery (mDNS / DNS-SD) | Consumer |
| [APP-ACTIVATION.md](APP-ACTIVATION.md) | `APP-ACTIVATION` | Deep-Link URI Handling & Single-Instance Launch | Consumer |
| [ICON-SET.md](ICON-SET.md) | `ICON-SET`, `ICON-RENDER` | Unified Iconography & Accessible Control Labels | Consumer |
| [PAGE-CONTENT.md](PAGE-CONTENT.md) | `PAGE-CONTENT`, `PAGE-STYLE`, `SITE-FAMILY-MAP` | GitHub Pages Design System & Family Navigation | Consumer |
| [REPO-STAMP.md](REPO-STAMP.md) | `REPO-STAMP`, `REPO-LAYOUT` | Repository Declarations & Canon Interface | Consumer, P/C |
| [CHECK-VERDICT.md](CHECK-VERDICT.md) | `CHECK-VERDICT`, `BUILD-EVIDENCE` | Quality Scripts, Exit Codes & Release Gates | Consumer |
| [INSTALL-TRUST.md](INSTALL-TRUST.md) | `INSTALL-TRUST` | Unsigned Installer Warning & Trust Guidance | Producer (adopted 2026-09-24, SP-0105) |
| [DOC-QUALITY.md](DOC-QUALITY.md) | `DOC-INTERNAL-QUALITY` | Internal Documentation Registry, Links & Sync | Consumer (adopted 2026-09-26, gated by SP-0140) |
| [CAPTURE-OUTPUT.md](CAPTURE-OUTPUT.md) | `CAPTURE-OUTPUT` | Produced Files: Kind, Prefix, Name, Folder and Format | Producer (read 2026-09-26; deviations by exception, aligned by SP-0179) |
| [PACKAGE-VERSIONING.md](PACKAGE-VERSIONING.md) | `PACKAGE-VERSIONING` | Package Version Timestamping & Parity | Consumer |
| [INPUT-PARITY.md](INPUT-PARITY.md) | `INPUT-PARITY` | Device Input Parity & Media Key Controls | Consumer (media keys contributor) |
| [SITE-STRUCTURE.md](SITE-STRUCTURE.md) | `SITE-STRUCTURE` | Product Site Page Set, Reach, Addresses & Locales (draft 0.1) | Consumer, page tier (opted in 2026-10-06, SP-0192) |
| [SITE-EXPERIENCE.md](SITE-EXPERIENCE.md) | `SITE-EXPERIENCE` | Product Site Look, Behaviour & Reach (draft 0.1) | Consumer, page tier (opted in 2026-10-06, SP-0192) |
| [SITE-REPRESENTATION.md](SITE-REPRESENTATION.md) | `SITE-REPRESENTATION` | Product Site Facts, Editions & Trust Claims (draft 0.1) | Consumer, page tier (opted in 2026-10-06, SP-0192) |
| [WINDOWS-STORE.md](WINDOWS-STORE.md) | `WINDOWS-STORE` | Microsoft Store Package, Listing Import File & Listing Content | **Owner**, P (written 2026-10-03; rungs 3-4 open, registry exception until 2026-11-03) |

Five of these are **owned here** (`APP-BEHAVIOUR`, `APP-STYLE`, `USER-PLAYLIST`, `DIAGNOSTIC-REPORT`, `WINDOWS-STORE`). That changes what a disagreement means: for a contract this product only
consumes, a difference is this repository's defect until the owner says otherwise; for one it owns, a
difference is an amendment this repository has to write, in the store, before the code moves - including
when the defect was found in somebody else's product.
