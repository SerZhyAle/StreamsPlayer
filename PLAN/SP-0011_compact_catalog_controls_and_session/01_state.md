# Phase 01 — Persisted browsing session

**Produces:** Backward-compatible local state fields and a contract test.

1. [Done] Add plain persisted search, selector-value, and channel-anchor fields to the catalog state model with empty/default values.
2. [Done] Add a store round-trip test covering every new field.

Static check: the Core test proves values survive save/load and no WPF type enters Core.
