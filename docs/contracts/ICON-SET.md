# ICON-SET - pointer

| | |
| --- | --- |
| **Id** | `ICON-SET` (with `ICON-RENDER`, `ICON-EXTERNAL`) |
| **Version** | 0.28 / 0.18 / 0.12 (draft) - read 2026-10-07; the map and the 48 vendored glyphs are at 0.28 / 0.18 / 0.12 (SP-0207) |
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

**0.28 / 0.18 / 0.12, read 2026-10-07 (SP-0206).** The catalog read that found the nine records and the stale stamps; what
it owed became SP-0207. Closed in SP-0206: 0.22 item E's qualified minimize name in Russian and Ukrainian
(`CompactPanelMinimizeTip`, "Свернуть окно" / "Згорнути вікно").

**0.28 / 0.18 / 0.12, implemented 2026-10-07 (SP-0207).** The nine `pending` entries are `meanings` drawing the catalog's
vendored drawings (`media.record`, `media.capture-frame`, `nav.minimize` - the former `window.minimize` key, 0.22 item E.1 -
`media.exit-picture-in-picture`, `source.own`, `source.hidden`, `app.language`, `action.choose-folder`, `app.theme`);
`pending` is empty and `assets/glyphs/pending/` is deleted. The chrome reason cites 0.22 item E.3, and the site theme switch
draws `app.theme` where it kept `PAGE-STYLE`'s `◐`, labelled with a qualified form of the record's "Switch theme". The
compact-panel accessible name is the declared qualified label "Compact panel" (0.22 item E.2) in en/ru/uk, the further
languages keeping this product's own forms as the record's note permits. The `ICON-RENDER` row declares this product
pointer-first (0.17); the rule-5 and rule-8 registry exception rows are closed and the compact-panel row narrowed to the
ru/uk "More actions" wording. Evidence: `tools/Sync-IconGlyphs.ps1 -Check` exit 0 - "48 meanings, 0 pending, 8 private -
in sync with the catalog"; 2055 tests pass, the ten `IconographyConformanceTests` among them.
