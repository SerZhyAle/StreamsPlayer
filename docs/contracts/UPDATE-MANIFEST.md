# UPDATE-MANIFEST - pointer

| | |
| --- | --- |
| **Id** | `UPDATE-MANIFEST` |
| **Version** | 0.12, 2026-10-22 (SP-0232) |
| **Home** | `Contracts/app-update-feed/README.md` |
| **Owner** | sza.od.ua hub |
| **This product's role** | Declared, not bound: no manifest client/producer |

ProductInfo reads its own display version. The site resolves a GitHub Releases link without an
installed-version comparison; that is outside this feed. No cache/hash/version-order client is
implemented, so the release-stamp/Android-versionCode amendment adds no current obligation.
A future client needs scoped adoption and negative version/remap/overflow tests before a claim.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
