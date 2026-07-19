# Phase 2 — Product and privacy pages

**Consumes:** phase 1 shared CSS and JavaScript.  
**Produces:** redesigned localized home and privacy pages.

- [x] Rebuild `docs/index.html` around a compact StreamsPlayer hero, truthful
  release CTA, capability cards, and distribution-status section using semantic
  landmarks and translation keys.
  - Check: `rg 'data-i18n|features|distribution|site.js' docs/index.html`
- [x] Rebuild `docs/privacy.html` with the shared header, language/theme controls,
  localized privacy content, and shared footer.
  - Check: `rg 'data-page="privacy"|data-i18n|site.js' docs/privacy.html`
- [x] Confirm every local stylesheet, script, icon, and internal-page reference
  used by both pages resolves to a file under `docs/`.
  - Check: PowerShell path-existence scan reports zero missing local references.
