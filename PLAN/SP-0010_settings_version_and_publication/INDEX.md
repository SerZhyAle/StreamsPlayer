# SP-0010 tactical plan

**Status:** BlockExternal

**Block:** Implementation and local checks are complete; Windows SDK packaging and unpublished public endpoints remain external verification gates.

| Phase | Produces | Consumes |
| --- | --- | --- |
| [01_settings_state.md](01_settings_state.md) | Persisted settings contract and compact bilingual window | Approved specification |
| [02_runtime_integration.md](02_runtime_integration.md) | Live tile sizing and preview lifecycle control | Phase 01 |
| [03_version_contract.md](03_version_contract.md) | Consistent calendar version across build/release/package paths | Approved specification |
| [04_publication_copy.md](04_publication_copy.md) | Store and winget submission text/checklists | Research dossier and implemented features |
| [05_validation.md](05_validation.md) | Static, automated, and observed evidence plus audit | Phases 01-04 |

Coverage: phases 01-02 satisfy criteria 1-4; phase 03 satisfies criterion 5; phase 04 satisfies criteria 6-7; phase 05 satisfies criterion 8 and audits the complete request.
