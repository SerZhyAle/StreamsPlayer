# ICON-SET - pointer

| | |
| --- | --- |
| **Id** | `ICON-SET` (with `ICON-RENDER`, `ICON-EXTERNAL`) |
| **Version** | 0.17 / 0.15 / 0.11 (draft) |
| **Home** | `Contracts/iconography/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer** - amends by `PROPOSAL-*` beside the contract, never by edit |

This repository keeps no copy of the vocabulary. Its side of the contract (SP-0113):

- `src/StreamsPlayer.App/Glyphs.map.json` - the product map: every glyph surface to one vocabulary id, a
  pending meaning (asked for in `PROPOSAL-2026-09-24-streamsplayer-meanings.md`), chrome/illustration, or
  an external picture.
- `assets/glyphs/` - the catalog glyphs this product draws, copied as-is, with `PROVENANCE.txt`.
- `tools/Sync-IconGlyphs.ps1` - imports them and generates `src/StreamsPlayer.App/Glyphs.xaml` and the
  README copies in `docs/assets/glyphs/`; `-Check` compares with the catalog where it is mounted.
- `tests/StreamsPlayer.Core.Tests/IconographyConformanceTests.cs` - holds the map, the copies, the markup,
  the names, the theme contrast and the site against each other without the catalog.

Conformance and dated exceptions: the StreamsPlayer rows of `Contracts/_meta/REGISTRY.md`.
