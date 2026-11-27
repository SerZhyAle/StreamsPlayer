# CONTEXT-HELP - pointer

| | |
| --- | --- |
| **Id** | `CONTEXT-HELP` |
| **Version** | 0.4 (draft) - read 2026-11-15 (SP-0278); 0.1 read 2026-10-21 |
| **Home** | `Contracts/in-app-help/README.md` |
| **Owner** | shared, the catalog owner working through the canon session |
| **This product's role** | **consumer** - amends by `PROPOSAL-*` beside the contract, never by edit |

This repository keeps no copy of the contract. Its side of it (SP-0230):

- `help/help-map.json` - the help map of rule 17: one row per `Window` class and per settings page, its form
  (`page`, `inherit` with a reason, `none` with a reason), its help id and anchor. Embedded in
  `StreamsPlayer.Core`, so the resolver and the gate read the same bytes.
- `help/help-baseline.txt` - accepted debt, which may only fall (`CHECK-BASELINE`): pages not yet written, the ten
  locales that open the English page, pictures not yet generated.
- `help/content/<locale>/<help-id>.json` - the copy of each page; names come from the UI dictionaries, prose from here.
- `tools/help/build-help.ps1` - renders the copy into `docs/help/<locale>/`, the set that ships beside the
  executable (`help\`) and is published from the same files.
- `tools/help/capture-help.ps1` - regenerates the External control pictures from the Windows release publish
  into `docs/help/`, with their bounds, hashes and capture stamp in `help/shots/`. The help renderer links
  each marker to its entry and serves the matching theme and language. The release checklist runs live
  freshness, and the fast help checks judge the generated files offline.
- `StreamsPlayer.Core` - `HelpMap`, `HelpResolver`, `HelpPageLocator`, `BrowserCommand`; `StreamsPlayer.App/HelpService.cs`,
  `BrowserLauncher.cs` and the one `ApplicationCommands.Help` binding on `Window` carry F1 and the `?` button
  (`HelpGlyphButton`, the `app.help` glyph of `ICON-SET`). The page opens in the default browser started from its
  registered command, because the shell drops the `#anchor` of a `file:` address.
- `scripts/check-help.ps1` and `tests/StreamsPlayer.Core.Tests/HelpGateTests.cs` - rule 19: surfaces against the map,
  pages against the locale set, links resolved, reasons present, the baseline.

Decisions the contract leaves to the product (SP-0230): every non-trivial surface is a `page`, opened in the system
browser from the copy beside the executable; no `layer` surface exists; Settings has one page with a section per
settings page; pages are authored in en, ru and uk and the other ten locales open the English page.

Item G (SP-0282): `tools/help/build-help.ps1` writes `<meta name="robots" content="noindex">` in the one head
template, `scripts/check-help.ps1` fails a page without it, and `docs/help/` was regenerated; the pages stay out of
the sitemap and `robots.txt` does not block them. The same gate holds the baseline against its copy at `HEAD`.

Conformance and dated exceptions: the StreamsPlayer row of `Contracts/_meta/REGISTRY.md`. Held at rung 1 for
slice S1; rungs 2 (generated pictures) and 3 (the walk) are open, and so are the pages of slices S2 to S4.
