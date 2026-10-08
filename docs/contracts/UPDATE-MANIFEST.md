# UPDATE-MANIFEST - pointer

| | |
| --- | --- |
| **Id** | `UPDATE-MANIFEST` |
| **Version** | 0.11 (draft) - read 2026-10-07 |
| **Home** | `Contracts/app-update-feed/README.md` |
| **Owner** | sza.od.ua hub |
| **This product's role** | **declared, not bound** - the contract's header names StreamsPlayer as a consumer, but this product has no update-check client |

## What this repository must do to stay conformant

Nothing binds yet. There is no update-check client in `src/`: the only version this product reads at run time is its own, as display
text (`ProductInfo.cs`), and the site resolves the installer link from the GitHub Releases API without comparing versions
(`tools/site/templates/site.js`). A link that resolves a release through that API or a package-manager manifest does not implement
this feed (contract section 1).

Until a client is written, the first two lines of the earlier version of this page ("resolves `streams-player.json`", "degrades
silently") were a plan, not a fact, and are withdrawn. The day a client is written it must follow the contract's rules, among them:

1. **Rule 6 (0.11), version order.** Compare part by part as decimal integers: leading zeros carry no meaning (`26.1003.0030` is
   26, 1003, 30), a ten-digit compact stamp is one part and needs a wide integer, never a text comparison; a version of any other
   shape makes the client offer nothing.
2. **Silent degradation.** A network failure or unparseable manifest must not block startup or raise a modal error.
3. **Integrity.** Verify the SHA-256 before offering or running an installer.
4. **Tests.** Fixtures for rule 6 (section 4) arrive with the client, not after it.

Evidence that none exists: a search of `src/`, `tools/`, `scripts/`, `installer/` and `.github/` for the manifest name, the
contract id and a version comparison finds nothing (2026-10-07, SP-0206).
