# SP-0003 GitHub README Localization

**Status:** Verified

## Goal

Make the StreamPlayer GitHub landing documentation feel like the author's other
product pages and provide an obvious English, Russian, and Ukrainian entry point
without relying on unsupported GitHub README scripting.

## Non-goals

- Changing application behaviour, packaging, or release state.
- Publishing the repository, Pages site, or a release.
- Replacing detailed engineering documentation outside the repository README.

## Constraints

- Preserve current factual product claims and the explicit-refresh/local-data
  contract.
- Keep all README content fully usable in GitHub's Markdown renderer and local
  clones.
- Use reciprocal EN/RU/UA navigation rather than JavaScript language controls.
- Keep the existing GitHub Pages language controls intact and verify them after
  documentation work.
- Do not represent planned distribution channels as published downloads.

## Acceptance criteria

1. The main GitHub README is a clear product entry point with StreamPlayer
   identity, concise capability-led sections, direct project links, and a
   visible EN/RU/UA selector.
2. Complete Russian and Ukrainian README variants exist with reciprocal
   language links and consistent product facts.
3. Each README tells prospective users that packaging channels are not yet
   published and directs them to the website/repository for status.
4. Every local Markdown link and image reference resolves, and GitHub-oriented
   Markdown renders without script or CSS dependencies.
5. The Pages site retains working RU/EN/UA selection and no horizontal overflow
   at mobile width.

## Research

See `tmp/SP-0003-github-readme/research.md`.

## Last Audit

- PASS — GitHub product entry. expected: concise product-first main README with
  icon, identity, actions, and language selector | actual: `README.md` presents
  all four and follows the reference's compact product hierarchy.
- PASS — static language navigation. expected: complete EN/RU/UA entry points |
  actual: `README.md`, `README.ru.md`, and `README.uk.md` have reciprocal links
  and equivalent capability, development, privacy, and ownership content.
- PASS — distribution truthfulness. expected: no unpublished package presented
  as downloadable | actual: every README labels ZIP, Store, and winget as planned
  rather than published.
- PASS — local Markdown references. expected: every local README link/image
  resolves | actual: path scan reported `Missing local README references: 0`.
- PASS — Pages language selection. expected: EN/RU/UA at mobile width | actual:
  browser automation selected each language; its active controls were RU and UA
  after the respective switches.
- PASS — Pages responsive behaviour. expected: no 390 px horizontal overflow |
  actual: home page reported `clientWidth = 390` and `scrollWidth = 390`; the
  Ukrainian mobile screenshot was visually inspected.

Evidence: `tmp/SP-0003-github-readme/mobile-uk.png` and the browser automation
output recorded during this run.
