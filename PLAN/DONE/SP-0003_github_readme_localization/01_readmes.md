# Phase 1 — GitHub-native localized README set

**Produces:** a product-oriented English README and Russian/Ukrainian variants.

- [x] Rewrite `README.md` as the English landing README with product icon,
  reciprocal language links, concise scenarios/capabilities, status-safe
  distribution wording, and developer commands.
  - Check: `rg 'README.ru.md|README.uk.md|Release status|Explicit refresh' README.md`
- [x] Add `README.ru.md` and `README.uk.md` with the same product facts,
  reciprocal EN/RU/UA navigation, website/source links, and localized usage and
  development sections.
  - Check: `rg 'README.md|README.ru.md|README.uk.md' README.ru.md README.uk.md`
