# STREAM-BANK - pointer

| | |
| --- | --- |
| **Id** | `STREAM-BANK` |
| **Version** | 2.4, 2026-10-22 (SP-0232) |
| **Home** | `Contracts/stream-catalog/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | Consumer of published ZIP and optional artwork |

`StreamBankReader`, `StreamCatalogCsvParser`, `CatalogMerger`, `UserAuthoredChannels`, artwork
readers and PublishWindowRetry hold the published boundary. Refresh stays explicit; MANUAL/IMPORTED
rows remain protected. ArtworkManifest.EnsureSupported already refuses higher schemas before fetch.
The adoption is partial: rule 14's closed language/country vocabulary is not implemented by the raw
parser/permissive facets; SP-0239 obtains the producer's vocabulary and vectors before migration.
Collection JSON is not consumed, so unknown collection versions do not rewrite local collections.
The Android source-normalizer/artwork rollout is not modified from this repository.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
