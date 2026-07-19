# Phase 01 — Contracts and state

1. [x] Add a platform-neutral command-line request parser for `--url` and `--id`,
   including validation and a testable error result.
   - Static check: parser tests cover valid URL, valid GUID, unknown option,
     missing option value, and invalid URL. PASS.
2. [x] Persist an optional last selected channel GUID in `CatalogState` and cover
   its JSON round trip.
   - Static check: existing state load/save preserves a non-default GUID. PASS.
