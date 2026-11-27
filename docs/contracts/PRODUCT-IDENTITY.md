# PRODUCT-IDENTITY - pointer

| | |
| --- | --- |
| **Id** | `PRODUCT-IDENTITY` |
| **Version** | 0.4 (draft), read 2026-11-20 (STRWIN-0301) |
| **Home** | `Contracts/product-identity/README.md` |
| **Owner** | shared |
| **This product's role** | Consumer |

One master of the product mark, every picture of the product generated from it and listed with its
SHA-256 in `assets/identity/identity-manifest.json`. The master is the vector SVG
`assets/identity/master/mark.svg` (`master.svg`). The one generator is
`tools/identity/build-identity.ps1` (`-Compose` also runs the social-card and Store composers, `-Check`
compares without writing); never edit a rendition or the manifest by hand. The help pages take the
`help.head` renditions and the ICON-SET glyphs through `tools/help/build-help.ps1`. Public channel
assets and the family-map card update together in the same authorized release (rule 10).

Conformance and the open items are this product's row in the catalog registry, not here.
