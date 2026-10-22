# PAGE-CONTENT / PAGE-STYLE / SITE-FAMILY-MAP - pointer

| | |
| --- | --- |
| **Id** | `PAGE-CONTENT`, `PAGE-STYLE`, `SITE-FAMILY-MAP` |
| **Version** | 1.5 / 3.4 / 1.9, 2026-10-22 (SP-0232) |
| **Home** | `Contracts/product-web-pages/README.md` |
| **Owner** | sza.od.ua hub |
| **This product's role** | Consumer: generated site and offline help |

`tools/site/build-site.ps1` renders the site from its templates/decks. `_head.html` accepts canonical
lang and legacy l queries with valid-lang precedence, preserving other parameters/anchors. Footer
names use the current family map. Offline help keeps its help footer and needs no remote fonts/grid.
The served CSS is the legacy 1.6 kit, honestly pinned by kit-provenance.txt; re-vendoring the current
3.4 kit and all covered components belongs to SP-0231 P5, due 2027-03-31. Source sync and a rendered
sample do not certify full PAGE-STYLE adoption. Shared portal/server-page routes follow section 2a;
the new server mark surface is SP-0234's, not another product card.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
