# SETTINGS-NAVIGATION - pointer

| | |
| --- | --- |
| **Id** | `SETTINGS-NAVIGATION` |
| **Version** | 2.0 (active), read 2026-10-22 for SP-0231 |
| **Home** | `Contracts/settings-navigation/README.md` |
| **Owner** | shared (portfolio) |
| **This product's role** | **consumer**; migration SP-0231, dated plan with deadline 2027-03-31 recorded in the registry's "StreamsPlayer unified UI migration" entry |
| **Function** | Compact page rail, at most one child level, ordered pages, the complete About set, stable and measured page presentation |

## What this repository must do to stay conformant

The rules are in the home page and are not repeated here. Repo-local facts:

- **Implementation (target):** the page rail and content pane of `SettingsWindow` (six pages, About last, identity-coloured rail glyphs, no child level), the About page, and the page-timing instrument added in phase P2.
- **Not yet conformant.** The rail's row height and glyph size are literals, the selected state is not asserted through UI Automation, the narrow-width adaptation is undocumented, About lacks support and contact, download and update information, a resource index and related products, and no page timing has ever been measured.

A plan is not conformance: this product's adoption row for this contract stays `-` until a measured result exists.
