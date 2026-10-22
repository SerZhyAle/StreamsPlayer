# LAN-DISCOVERY - pointer

| | |
| --- | --- |
| **Id** | `LAN-DISCOVERY` |
| **Version** | 0.11, 2026-10-22 (SP-0232) |
| **Home** | `Contracts/lan-discovery/README.md` |
| **Owner** | Shared |
| **This product's role** | Declared, not bound: no announcer or mDNS browser |

No discovery code or mDNS dependency exists in src/. Broadcasts are imported by description and
connected through explicit endpoints under LIVE-BROADCAST/DEVICE-EXCHANGE. The old 0.10 consumer
claim and its invented broadcast/SFTP discovery capability are withdrawn. Reserved services are not
implemented. A future browser/announcer owes a new scoped adoption and mock/emission evidence.

The [synchronization audit](../audits/SP-0232-contract-sync.md) records clause scope, evidence and remaining work. Own catalog registry rows remain
the conformance authority; a read version does not upgrade an implementation claim.
