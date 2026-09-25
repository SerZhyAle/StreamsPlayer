# docs/contracts/

Pointers, never copies. Every file here names one shared contract: its id, its version, its home in the
cross-project contract store (`P:\Contracts`), this product's role, and what this repository must do to stay conformant.

The contract store's location is named in exactly one tracked file - `CLAUDE.md`, first line. Everything
here cites a contract by its path under `Contracts/`, and source comments cite it by document and item -
`10_contract_amendment_2026-08-20.md item D`, `STREAM-BANK rule 5` - because whoever clones this
repository has no such drive.

A pointer that grows a second page has become a copy. If this repository believes a contract is wrong, the
amendment is written in the store, not here.

| File | Contract ID | Function | Role |
| --- | --- | --- | --- |
| [APP-BEHAVIOUR.md](APP-BEHAVIOUR.md) | `APP-BEHAVIOUR` | Desktop Application UX Behavior (12 shared moments) | **Owner**, P/C |
| [APP-STYLE.md](APP-STYLE.md) | `APP-STYLE` | Desktop Palette Vocabulary and Dynamic Theming | **Owner**, P/C |
| [USER-PLAYLIST.md](USER-PLAYLIST.md) | `USER-PLAYLIST` | User Stream Playlists & Extended M3U8 Interchange | **Owner**, P/C |
| [DIAGNOSTIC-REPORT.md](DIAGNOSTIC-REPORT.md) | `DIAGNOSTIC-REPORT` | Diagnostic Bundle, Environment Summary & Redaction | **Owner**, P/C |
| [STREAM-BANK.md](STREAM-BANK.md) | `STREAM-BANK` | Published Stream Catalog ZIP & Artwork Atlas | Consumer |
| [LIVE-BROADCAST.md](LIVE-BROADCAST.md) | `LIVE-BROADCAST` | Live Stream Descriptor & Direct Response Playback | Consumer |
| [WAVE-PARTICLES.md](WAVE-PARTICLES.md) | `WAVE-PARTICLES` | "Particles and Lines" Animated Backdrop Simulation | Consumer |
| [MEDIA-CLASSIFICATION.md](MEDIA-CLASSIFICATION.md) | `MEDIA-CLASSIFICATION` | Media Kind Resolution and Stream Classification | Consumer |
| [UPDATE-MANIFEST.md](UPDATE-MANIFEST.md) | `UPDATE-MANIFEST` | Non-Store App Update Feed & Integrity Check | Consumer |
| [LAN-DISCOVERY.md](LAN-DISCOVERY.md) | `LAN-DISCOVERY` | Local Network Service Discovery (mDNS / DNS-SD) | Consumer |
| [APP-ACTIVATION.md](APP-ACTIVATION.md) | `APP-ACTIVATION` | Deep-Link URI Handling & Single-Instance Launch | Consumer |
| [ICON-SET.md](ICON-SET.md) | `ICON-SET`, `ICON-RENDER` | Unified Iconography & Accessible Control Labels | Consumer |
| [PAGE-CONTENT.md](PAGE-CONTENT.md) | `PAGE-CONTENT`, `PAGE-STYLE`, `SITE-FAMILY-MAP` | GitHub Pages Design System & Family Navigation | Consumer |
| [REPO-STAMP.md](REPO-STAMP.md) | `REPO-STAMP`, `REPO-LAYOUT` | Repository Declarations & Canon Interface | Consumer, P/C |
| [CHECK-VERDICT.md](CHECK-VERDICT.md) | `CHECK-VERDICT`, `BUILD-EVIDENCE` | Quality Scripts, Exit Codes & Release Gates | Consumer |
| [INSTALL-TRUST.md](INSTALL-TRUST.md) | `INSTALL-TRUST` | Unsigned Installer Warning & Trust Guidance | Producer (adopted 2026-09-24, SP-0105) |
| [DOC-QUALITY.md](DOC-QUALITY.md) | `DOC-INTERNAL-QUALITY` | Internal Documentation Registry, Links & Sync | Consumer (adopted 2026-09-26, gaps in SP-0140) |

Four of these are **owned here** (`APP-BEHAVIOUR`, `APP-STYLE`, `USER-PLAYLIST`, `DIAGNOSTIC-REPORT`). That changes what a disagreement means: for a contract this product only
consumes, a difference is this repository's defect until the owner says otherwise; for one it owns, a
difference is an amendment this repository has to write, in the store, before the code moves - including
when the defect was found in somebody else's product.
