# BOOLEAN-OPTION - pointer

| | |
| --- | --- |
| **Id** | `BOOLEAN-OPTION` |
| **Version** | 2.0 (active), read 2026-10-22 for SP-0231 |
| **Home** | `Contracts/boolean-option/README.md` |
| **Owner** | shared (portfolio) |
| **This product's role** | **consumer**; migration SP-0231, dated plan with deadline 2027-03-31 recorded in the registry's "StreamsPlayer unified UI migration" entry |
| **Function** | One compact checkbox and explanation row: the heading toggles once, the explanation and help never do, optional local help from one authored source |

## What this repository must do to stay conformant

The rules are in the home page and are not repeated here. Repo-local facts:

- **Implementation (target):** `BooleanOptionRow` in `StreamsPlayer.App`, used for all thirteen checkboxes: eleven in Settings, one in `AddStreamWindow` and one in `BroadcastAudioWindow`.
- **Not yet conformant.** Phase P1 built the component and adopted it for the animated-backdrop option; P2 adopts the other twelve. The help popups are built and not yet observed, because no option uses help yet. Local option help shares its authored text with `CONTEXT-HELP` (`help/content`).

A plan is not conformance: this product's adoption row for this contract is `partial` and advances only with a measured result (registry, 2026-10-22).
