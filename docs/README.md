# docs/

Index of this tree. Two very different things live here, and the split matters:

- **The published GitHub Pages site** - `index.html`, `privacy.html`, `site.js`, `style.css`,
  `robots.txt`, `sitemap.xml`, the `assets/` it references, and one folder per non-English shipped
  language (`ru/`, `uk/`, `de/`, ..). **All of it is generated** by
  `tools/site/build-site.ps1` from `tools/site/templates/` plus one copy deck per language in
  `tools/site/copy/<code>.txt`. Hand-editing any of it is a silent revert on the next run. The
  language list is never written in the generator - it comes from `InterfaceLanguages` in
  `StreamsPlayer.Core`. Verify with `pwsh -NoProfile -File tools/site/build-site.ps1 -Check`, which
  writes nothing and fails if `docs/` is stale. The link-preview card `assets/og-card.png` is
  generated separately by `tools/site/make-og-image.ps1`, and the lead screenshot under the hero,
  `assets/screens/grid-<code>.jpg` (the catalog as a grid of video channels, one per language), by
  `tools/site/export-site-screenshots.ps1` from the Store captures in `assets/store/`.
- **The in-app help** - `help/<locale>/*.html` with `help.css` and the vendored `sza-kit.css` (SP-0230,
  `CONTEXT-HELP`). Also **generated**, by `tools/help/build-help.ps1` from the help map and the copy in
  the repository's `help/` folder and the UI dictionaries; the same folder is copied beside the executable
  in every package, so the application opens its own copy with no network. Verify with
  `build-help.ps1 -Check` and `scripts/check-help.ps1`. It is not a site page: the site generator
  neither writes nor reads it.
- **Hand-written documentation** - everything below. Edit these directly.

[`DOCUMENT_REGISTRY.jsonl`](../DOCUMENT_REGISTRY.jsonl) at the root declares the roots of the maintained
corpus (a directory ends in `/`, a file is named whole) and its reasoned ignores. Every document under a
root - these, the generated pages above, and every other `*.md` - has one entry there. `scripts/check-docs.ps1`
fails on an undeclared document under a root, a broken link or anchor, and house-style breaks (SP-0140,
SP-0287), so a new document lands with its entry and a new documentation folder with its root. A file under
no root is outside the corpus: the gate names it in a `note:` line and does not judge it.

## Maintainer documentation

| Path | What it is |
| --- | --- |
| [PLAYBACK_RESILIENCE.md](PLAYBACK_RESILIENCE.md) | How StreamsPlayer fights a bad source: retry, buffering and failure surfacing. |
| [stream-playback-recommendations.md](stream-playback-recommendations.md) | Recommendations aimed at the FastMediaSorter (Android) side, about the stream bank this app consumes. |
| [fastmediasorter-playback-recommendations.md](fastmediasorter-playback-recommendations.md) | The reverse direction: what this player could adopt from FastMediaSorter's Media3 playback. |
| [localization/glossary.md](localization/glossary.md) | Translation glossary for the shipped interface languages. |
| [contracts/](contracts/README.md) | One pointer per shared contract this product consumes or owes. Pointers, never copies. |
| [cli-commands.json](cli-commands.json) | The command manifest (`APP-COMMANDS` rule 18): every command `StreamsPlayer.exe` accepts from scripts and other programs. The one source of truth - embedded in the build and printed by `commands --json`; each command's `exe` names the executable that serves it, and `StreamsPlayerCli.exe` is the console companion a pipeline reads. |
| [CLI.md](CLI.md) | The command-line reference, **generated** from `cli-commands.json` by `tools/Build-CliDoc.ps1` (`APP-COMMANDS` rule 20; never edit by hand): the command table and, per command, its synopsis, options, risk, exit codes and `cmd` and PowerShell examples. `scripts/check-fast.ps1` fails when it is stale. |

## Agent workflow

[agent/](agent/) holds the method documents the repository's agent rules point at -
`SPEC_LIFECYCLE`, `CODE_QUALITY`, `VALIDATION`, `RESEARCH_INDEX`, `AGENT_MEMORY`, `COST`.
Universal conventions are **not** here: they live in the SZA Unified Rules canon, which ships as the
`sza` plugin. `AGENTS.md` at the repository root is the authoritative contributor guide.
