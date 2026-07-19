# Phase 1 — Shared design system

**Produces:** reusable visual tokens, layout primitives, responsive rules, and
browser behaviour consumed by both pages.

- [x] Replace `docs/style.css` with a local StreamPlayer adaptation of the SZA
  Pine + Gold design system, including dark/light tokens, glass header, hero,
  buttons, cards, distribution blocks, footer, breakpoints, focus states, and
  reduced-motion behaviour.
  - Check: `rg -- '--gold|data-theme="light"|prefers-reduced-motion|site-header' docs/style.css`
- [x] Add `docs/site.js` with shared RU/EN/UA translations, browser-language
  resolution, local theme/language persistence, theme metadata updates, and the
  back-to-top interaction.
  - Check: `rg 'streamplayer-theme|streamplayer-lang|setLanguage|toggleTheme' docs/site.js`
