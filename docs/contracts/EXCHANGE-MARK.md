# EXCHANGE-MARK - pointer

| | |
| --- | --- |
| **Id** | `EXCHANGE-MARK` |
| **Version** | 0.3 (draft), vendored 2026-10-22 (SP-0234) |
| **Home** | `Contracts/exchange-mark/README.md` |
| **Owner** | shared |
| **This product's role** | Consumer |

The mark of the exchange server, drawn nowhere in this repository: the coloured mark is the catalog's PNG steps copied
byte for byte into `assets/exchange-mark/` (pinned in its `PROVENANCE.txt`, written by `tools/Sync-ExchangeMark.ps1`),
and the one-tone glyph is the catalog's `exchange-mark-mono.svg` kept as `assets/glyphs/own/source.exchange-server.svg`,
which `tools/Sync-IconGlyphs.ps1` turns into the geometry of the main window's status button (rule 6, path data unchanged).

`ExchangeMarkIcon` draws the coloured mark at 24 px beside the Settings group heading and the change-password dialog, and
at 16 px in the guest-account rows, the status lines, the account's broadcast list and before the portal link; the PNG step
is the exact frame for the screen scale (`ExchangeMarkSteps`, rule 5) and gives way to the one-tone glyph under a Windows
high-contrast theme (rule 3). `feature.server-relay` keeps the server bays (section 6). System message boxes, the About
links, the READMEs and the Store listing carry no picture, with their reasons listed in SP-0234.

`ExchangeMarkConformanceTests` hold the copies against their provenance and the surfaces against the ticket's list;
`tools/Sync-ExchangeMark.ps1 -Check` and the catalog's `Find-ExchangeMark.ps1` compare them with the catalog where it is mounted.
The catalog registry row of this product is `partial 0.3` until the surfaces are walked on screen.
