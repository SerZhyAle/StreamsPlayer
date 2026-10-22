# SITE-STRUCTURE - pointer

| | |
| --- | --- |
| **Id** | `SITE-STRUCTURE` |
| **Version** | 0.3, 2026-10-22 (SP-0232) |
| **Home** | `Contracts/product-site/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | Consumer, page tier |

Landing, privacy, trust, support and history are generated in all 13 shipped locales, plus root
404. The 2026-10-17 amendment permits the existing Support type and static shipped-record history;
those two old exceptions are superseded. Footer contact/hub/support and history structure are gated
by Test-SiteDiscovery.ps1 and Test-ReleaseNotes.ps1. Held addresses stay fixed. Publication and the
live history-page answer remain unverified until an explicitly authorized publish (SP-0039).

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
