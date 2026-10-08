# INSTALL-TRUST - pointer

| | |
| --- | --- |
| **Id** | `INSTALL-TRUST` |
| **Version** | 1.2 (active) |
| **Home** | `Contracts/install-trust/README.md`, with the reference rendering in `TRUST_GUIDE.md` |
| **Owner** | shared - amendments through the home page |
| **This product's role** | **producer**, adopted 2026-09-24 (SP-0105) |

## Why it applies here

Three of the four download channels are unsigned - the Inno per-user setup EXE, the portable ZIP, and the
winget package built from that ZIP - so those users meet SmartScreen. `installer/StreamsPlayer.iss`
configures no signing tool and neither does `.github/workflows/release.yml`. Only the Store MSIX escapes
it, because Microsoft signs it at certification.

## Where this repository meets it

The page is `trust.html` on the product site - `docs/trust.html` and `docs/<code>/trust.html` in all
thirteen shipped languages. It is a render target: the source is the `trust-*` keys of
`tools/site/copy/<code>.txt` and `tools/site/templates/trust.html`, and `tools/site/build-site.ps1`
regenerates it. It is linked from the three READMEs and from the site's Distribution section and footer.

`InstallTrustPageTests` pins what is readable from the page: rule 1's four sections in order, rule 2's
quoted dialogs, and no rule-4 instruction in the English source. Rule 5 is met by the itemized
"Install for all users" elevation - the one the installer's install-mode dialog allows - and rule 6 by the
`trust-never-*` list, which says what the `privacy-*` keys and the Store data-safety answers
(`msix/store-listing.md`) say. Change one of those three and the other two change in the same edit.

**1.2, read 2026-10-07 (SP-0206).** Item K: the per-file "Unblock" checkbox is not a permitted instruction. No page, README, installer
text or localization carries one (the word appears only in the English template comment, which is not rendered text, and in two
unrelated source comments). `InstallTrustPageTests` now also fails on an "unblock" instruction in the English deck. Item L (the
reference rendering restructured) changes nothing here: the page already has the four sections in the contract's order.
