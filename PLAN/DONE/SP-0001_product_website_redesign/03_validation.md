# Phase 3 — Browser verification and audit

**Consumes:** the completed static site.  
**Produces:** desktop/mobile evidence and final ticket audit.

- [x] Serve `docs/` locally and capture the home page at desktop and mobile
  viewport sizes in a headless browser.
  - Check: screenshots exist under `tmp/SP-0001-site/` and visual inspection
    shows no clipping, broken layout, or missing local assets.
- [x] Exercise language and theme controls in a browser and inspect both pages.
  - Check: browser DOM evidence shows the selected language text and theme
    attribute after interaction/reload.
- [x] Audit live files against all strategic criteria and record PASS/WARN/FAIL/
  MANUAL evidence in the strategic ticket.
  - Check: ticket status and Last Audit match the observed result.
