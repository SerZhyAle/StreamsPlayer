# SP-0001 Product Website Redesign

**Status:** Verified

## Goal

Redesign the StreamPlayer product website in the visual language of the SZA
website while keeping StreamPlayer's product identity, factual claims, and
independent distribution status clear.

## Non-goals

- Publishing GitHub Pages or creating a product release.
- Changing the Windows application or catalog behaviour.
- Copying unrelated projects, author biography, or contact content from the
  reference page.

## Constraints

- Use the SZA Pine + Gold visual language: compact glass header, green primary
  accent, gold secondary accent, restrained background glow, pill controls, and
  responsive cards.
- Keep the site static and suitable for GitHub Pages.
- Keep release availability truthful: GitHub ZIP, Store, and winget remain
  future distribution channels until actually published.
- Preserve accessibility basics: semantic landmarks, keyboard focus, readable
  contrast, reduced-motion handling, and responsive layouts.
- Offer RU, EN, and UA content plus light/dark theme selection without a server.
- Apply the same site chrome and visual system to the privacy page.

## Acceptance criteria

1. The home page visibly follows the reference's Pine + Gold design system and
   identifies StreamPlayer immediately as a Windows stream player.
2. The primary product capabilities and explicit-refresh/local-data principles
   are concise and factually aligned with the repository README.
3. Distribution controls do not suggest that an unpublished release, Store
   listing, or winget package is currently available.
4. RU, EN, and UA controls update page content, persist locally, and default to
   the matching browser language when no choice is stored.
5. A light/dark control updates the palette, persists locally, and respects the
   initial system preference.
6. Home and privacy pages share coherent navigation, footer, responsive layout,
   and accessibility behaviour.
7. Desktop and mobile browser observations show no clipping, broken layout, or
   missing local assets.

## Risks

- Translation drift can make factual product claims inconsistent across
  languages.
- Browser-only controls can flash the wrong theme if preference resolution is
  delayed until page load.
- A close visual match can weaken the product identity unless the StreamPlayer
  icon and purpose remain prominent.

## Research

See `tmp/SP-0001-site/research.md` for the evidence dossier and reference
screenshot.

## Last Audit

- PASS — reference visual system. expected: Pine + Gold, glass header, compact
  hero, pill controls, cards | actual: desktop and mobile screenshots show all
  named patterns with StreamPlayer icon and content.
- PASS — product facts. expected: catalog controls, radio/video/RTSP, explicit
  refresh, local data | actual: all four facts match `README.md` and the privacy
  contract.
- PASS — distribution truthfulness. expected: no unpublished download presented
  as available | actual: the page says active development and labels ZIP,
  Microsoft Store, and winget as Planned/Preparing.
- PASS — localization. expected: RU/EN/UA selection and persistence | actual:
  browser automation selected RU, reloaded with RU, and selected UA with the UA
  control active.
- PASS — theming. expected: system-aware initial theme and persisted toggle |
  actual: dark initialized, light persisted after reload, and the light privacy
  page rendered correctly.
- PASS — responsive pages. expected: home and privacy at 390 px without clipping
  | actual: both reported `clientWidth = 390` and `scrollWidth = 390`.
- PASS — local assets. expected: zero missing local references | actual: the
  path-existence scan reported `Missing local references: 0`.
- PASS — JavaScript syntax. expected: parseable shared script | actual:
  `node --check docs/site.js` exited 0.

Evidence: `tmp/SP-0001-site/desktop.png`,
`tmp/SP-0001-site/mobile-emulated.png`,
`tmp/SP-0001-site/privacy.png`, and
`tmp/SP-0001-site/privacy-light-ru.png`.
