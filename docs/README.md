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
  generated separately by `tools/site/make-og-image.ps1`.
- **Hand-written documentation** - everything below. Edit these directly.

## Maintainer documentation

| Path | What it is |
| --- | --- |
| [PLAYBACK_RESILIENCE.md](PLAYBACK_RESILIENCE.md) | How StreamsPlayer fights a bad source: retry, buffering and failure surfacing. |
| [stream-playback-recommendations.md](stream-playback-recommendations.md) | Recommendations aimed at the FastMediaSorter (Android) side, about the stream bank this app consumes. |
| [fastmediasorter-playback-recommendations.md](fastmediasorter-playback-recommendations.md) | The reverse direction: what this player could adopt from FastMediaSorter's Media3 playback. |
| [localization/glossary.md](localization/glossary.md) | Translation glossary for the shipped interface languages. |
| [specifications/streams.txt](specifications/streams.txt) | The standalone product specification. |
| [specifications/competitor-improvement-backlog.md](specifications/competitor-improvement-backlog.md) | Competitor review and the improvement ideas it produced. |

## Agent workflow

[agent/](agent/) holds the method documents the repository's agent rules point at -
`SPEC_LIFECYCLE`, `CODE_QUALITY`, `VALIDATION`, `RESEARCH_INDEX`, `AGENT_MEMORY`, `COST`.
Universal conventions are **not** here: they live in the SZA Unified Rules canon, which ships as the
`sza` plugin. `AGENTS.md` at the repository root is the authoritative contributor guide.
