# MEDIA-CLASSIFICATION - pointer

| | |
| --- | --- |
| **Id** | `MEDIA-CLASSIFICATION` |
| **Version** | 0.12, 2026-10-22 (SP-0232) |
| **Home** | `Contracts/media-classification/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | Primary-category boundary not implemented; stream routing only |

`MediaKind` has Audio/Video/Rtsp and `StreamMediaKindClassifier` selects playback under STREAM-BANK
rule 4. It does not implement the eight-category file/URL enum of this contract: .m3u8 routing as
video is not the primary STREAM category. The prior whole-contract claim is withdrawn.
A dated proposal asks the owner to clarify this Windows scope. No image classifier or decoder
capability is claimed from the product's name in a consumer list.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
