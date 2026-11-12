# SITE-EXPERIENCE - pointer

| | |
| --- | --- |
| **Id** | `SITE-EXPERIENCE` |
| **Version** | 0.6, 2026-11-12 (SP-0259; read for rule 13, items E and F) |
| **Home** | `Contracts/product-site/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | Consumer, page tier |

Support/history use the accepted 2026-10-17 shapes. ReleaseNotes.ps1 reads versioned shipped-note
records newest first; since SP-0259 the landing renders its version, date, versioned asset links and their
.sha256 sidecars at build time from tools/site/release-manifest.json (rule 13 item E), and no page calls
api.github.com, so the privacy copy no longer names it. The generated
site has theme/locale/focus/width sample evidence, not complete current-kit acceptance.
The legacy kit and remaining covered UI migration are SP-0231 P5; its deadline is unchanged.
Privacy copy continues to name the site's actual external origins. Live publication is not claimed.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
