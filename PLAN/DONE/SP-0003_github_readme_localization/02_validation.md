# Phase 2 — Static and browser verification

**Consumes:** localized README files and the existing Pages language controls.

- [x] Check that all local Markdown links and image paths resolve and that no
  README presents an unpublished channel as a download.
  - Check: path scan reports zero missing local references; wording scan reports
    no active download claim.
- [x] Serve `docs/` and use a browser to verify RU/EN/UA selection and a 390 px
  home page without horizontal overflow.
  - Check: browser output includes each language and `scrollWidth = 390`.
- [x] Audit the live documentation against the strategic ticket.
  - Check: Last Audit has no WARN, FAIL, or MANUAL items.
