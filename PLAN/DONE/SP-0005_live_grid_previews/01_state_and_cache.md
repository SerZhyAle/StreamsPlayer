# Phase 01: State and two-layer cache

**Produces:** persisted view mode, capture eligibility, URL-keyed memory/disk cache.

**Status:** Completed

1. Extend `CatalogState` with a platform-neutral List/Grid preference and cover its JSON round trip in `StreamCatalogStoreTests`.
   - Static check: a non-default grid preference survives `SaveAsync` and `LoadAsync`.
2. Add App preview contracts and a 64-entry URL-keyed LRU whose freshness is independent of visibility.
   - Static check: restored entries are available but stale; a stale entry remains retrievable; the 65th distinct URL evicts the least-recently-used entry.
3. Add the dedicated JPEG store with SHA-256 URL lookup, background reads/writes, quality 75, and oldest-first cleanup above 64 files.
   - Static check: filenames contain only lowercase SHA-256 hex plus `.jpg`; public cache methods do not perform synchronous file reads or writes.
