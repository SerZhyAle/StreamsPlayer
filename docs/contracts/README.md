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
| [APP-SETTINGS.md](APP-SETTINGS.md) | `APP-SETTINGS` | Desktop Application Settings Surface Anatomy & Layout | Consumer; read 0.7, implementation retained; SP-0231 migration (SP-0232) |
| [APP-STYLE.md](APP-STYLE.md) | `APP-STYLE` | Desktop Palette Vocabulary and Dynamic Theming | **Owner**, P/C |
| [WINDOWS-UI.md](WINDOWS-UI.md) | `WINDOWS-UI` | Shared Windows settings, dialogs and working-viewer profile | Consumer; read 0.10, implementation partial; SP-0231 migration (SP-0232) |
| [UI-SIZING.md](UI-SIZING.md) | `UI-SIZING` | Shared size and input profiles: scale, targets, text enlargement | Consumer (read 2026-10-22 at 1.1, SP-0231: not yet conformant, deadline 2027-03-31) |
| [BUTTONS.md](BUTTONS.md) | `BUTTONS` | One mandatory command system for buttons, dialog answers and toolbars | Consumer (read 2026-10-22 at 2.0, SP-0231: not yet conformant, deadline 2027-03-31) |
| [BOOLEAN-OPTION.md](BOOLEAN-OPTION.md) | `BOOLEAN-OPTION` | One compact checkbox and explanation row | Consumer (read 2026-10-22 at 2.0, SP-0231: not yet conformant, deadline 2027-03-31) |
| [COLLAPSIBLE-GROUP.md](COLLAPSIBLE-GROUP.md) | `COLLAPSIBLE-GROUP` | Semantic groups with a state triangle and an in-flow body | Consumer (read 2026-10-22 at 1.1, SP-0231: not yet conformant, deadline 2027-03-31) |
| [SETTINGS-NAVIGATION.md](SETTINGS-NAVIGATION.md) | `SETTINGS-NAVIGATION` | Compact page rail, About set and measured page presentation | Consumer (read 2026-10-22 at 2.0, SP-0231: not yet conformant, deadline 2027-03-31) |
| [USER-PLAYLIST.md](USER-PLAYLIST.md) | `USER-PLAYLIST` | User Stream Playlists & Extended M3U8 Interchange | **Owner**, P/C |
| [BROADCAST-RESOURCE.md](BROADCAST-RESOURCE.md) | `BROADCAST-RESOURCE` | StreamsPlayer broadcast resource document (`.spres`) | Partial reader/player 1.0; physical LAN/backend acceptance unverified (SP-0232) |
| [DIAGNOSTIC-REPORT.md](DIAGNOSTIC-REPORT.md) | `DIAGNOSTIC-REPORT` | Diagnostic Bundle, Environment Summary & Redaction | **Owner**, P/C |
| [STREAM-BANK.md](STREAM-BANK.md) | `STREAM-BANK` | Published Stream Catalog ZIP & Artwork Atlas | Consumer |
| [LIVE-BROADCAST.md](LIVE-BROADCAST.md) | `LIVE-BROADCAST` | Live Stream Descriptor & Direct Response Playback | Consumer, and producer of audio (read at 1.3 on 2026-10-22, SP-0232; implementation partial 1.2, SP-0228: `partial`; item I for RTSP first picture measured on a real phone 2026-10-15, inside budget, steady state is the owner's remaining observation, SP-0208; no handshake of section 6 has been run against a live phone since) |
| [DEVICE-EXCHANGE.md](DEVICE-EXCHANGE.md) | `DEVICE-EXCHANGE` | Device Exchange: a user's devices finding broadcasts through a server account or Google Drive | Receiver and broadcaster, no resources, sign-in only (read at 1.7 on 2026-10-22, SP-0232; implementation partial 1.5, SP-0228: `partial` - no joint run has happened and the Drive half, SP-0202, is not built; the dated exceptions and the follow-up SP-0237 are in the pointer and the registry rows) |
| [WAVE-PARTICLES.md](WAVE-PARTICLES.md) | `WAVE-PARTICLES` | "Particles and Lines" Animated Backdrop Simulation | Consumer |
| [MEDIA-CLASSIFICATION.md](MEDIA-CLASSIFICATION.md) | `MEDIA-CLASSIFICATION` | Primary-category classification versus stream routing | Primary boundary absent; old claim withdrawn, SP-0232 |
| [UPDATE-MANIFEST.md](UPDATE-MANIFEST.md) | `UPDATE-MANIFEST` | Non-Store App Update Feed & Integrity Check | Read 0.12; not bound, no update client (SP-0232) |
| [LAN-DISCOVERY.md](LAN-DISCOVERY.md) | `LAN-DISCOVERY` | Local Network Service Discovery (mDNS / DNS-SD) | Not bound; no browser or announcer, SP-0232 |
| [APP-ACTIVATION.md](APP-ACTIVATION.md) | `APP-ACTIVATION` | Deep-Link URI Handling & Single-Instance Launch | Consumer |
| [ICON-SET.md](ICON-SET.md) | `ICON-SET`, `ICON-RENDER` | Unified Iconography & Accessible Control Labels | Consumer |
| [EXCHANGE-MARK.md](EXCHANGE-MARK.md) | `EXCHANGE-MARK` | The mark of the exchange server: the star, its two forms and the one-tone glyph (draft 0.3) | Consumer (vendored 0.3, SP-0234: `partial`, surfaces not yet walked on screen) |
| [CONTEXT-HELP.md](CONTEXT-HELP.md) | `CONTEXT-HELP` | In-app help: F1, the `?` button, the help map and the help pages (draft 0.1) | Consumer (read 2026-10-21, SP-0230: `partial`, slice S1) |
| [PAGE-CONTENT.md](PAGE-CONTENT.md) | `PAGE-CONTENT`, `PAGE-STYLE`, `SITE-FAMILY-MAP` | GitHub Pages Design System & Family Navigation | Consumer |
| [REPO-STAMP.md](REPO-STAMP.md) | `REPO-STAMP`, `REPO-LAYOUT` | Repository Declarations & Canon Interface | Consumer, P/C |
| [CHECK-VERDICT.md](CHECK-VERDICT.md) | `CHECK-VERDICT`, `BUILD-EVIDENCE` | Quality Scripts, Exit Codes & Release Gates | Consumer |
| [INSTALL-TRUST.md](INSTALL-TRUST.md) | `INSTALL-TRUST` | Unsigned Installer Warning & Trust Guidance | Producer (adopted 2026-09-24, SP-0105) |
| [DOC-QUALITY.md](DOC-QUALITY.md) | `DOC-INTERNAL-QUALITY`, `DOC-EXTERNAL-QUALITY` | Internal registry and scoped help/portal quality | Partial; read 0.10 / 0.12, SP-0232 |
| [CAPTURE-OUTPUT.md](CAPTURE-OUTPUT.md) | `CAPTURE-OUTPUT` | Produced Files: Kind, Prefix, Name, Folder and Format | Producer (read 2026-09-26; deviations by exception, aligned by SP-0179) |
| [PACKAGE-VERSIONING.md](PACKAGE-VERSIONING.md) | `PACKAGE-VERSIONING` | Package Version Timestamping & Parity | Consumer |
| [INPUT-PARITY.md](INPUT-PARITY.md) | `INPUT-PARITY` | Device Input Parity & Media Key Controls | Consumer (media keys contributor) |
| [SITE-STRUCTURE.md](SITE-STRUCTURE.md) | `SITE-STRUCTURE` | Product Site Page Set, Reach, Addresses & Locales (0.3, partial) | Consumer, page tier (opted in 2026-10-06, SP-0192) |
| [SITE-EXPERIENCE.md](SITE-EXPERIENCE.md) | `SITE-EXPERIENCE` | Product Site Look, Behaviour & Reach (0.4, partial) | Consumer, page tier (opted in 2026-10-06, SP-0192) |
| [SITE-REPRESENTATION.md](SITE-REPRESENTATION.md) | `SITE-REPRESENTATION` | Product Site Facts, Editions & Trust Claims (draft 0.1) | Consumer, page tier (opted in 2026-10-06, SP-0192) |
| [WINDOWS-STORE.md](WINDOWS-STORE.md) | `WINDOWS-STORE` | Microsoft Store Package, Listing Import File & Listing Content | **Owner**, P; partial 0.2 local listing/package evidence; fresh live import is SP-0239 |

Five of these are **owned here** (`APP-BEHAVIOUR`, `APP-STYLE`, `USER-PLAYLIST`, `DIAGNOSTIC-REPORT`, `WINDOWS-STORE`). That changes what a disagreement means: for a contract this product only
consumes, a difference requires implementation or an explicit justified exception and, where needed, an owner proposal; for one it owns, a
difference is an amendment this repository has to write, in the store, before the code moves - including
when the defect was found in somebody else's product.

The [2026-10-22 synchronization audit](../audits/SP-0232-contract-sync.md) maps all 29 targets to their
actual surfaces and names unresolved work. Read versions and implementation claims are separate.
