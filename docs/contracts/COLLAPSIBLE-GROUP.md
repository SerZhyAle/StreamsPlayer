# COLLAPSIBLE-GROUP - pointer

| | |
| --- | --- |
| **Id** | `COLLAPSIBLE-GROUP` |
| **Version** | 1.1 (active), read 2026-10-22 for SP-0231 |
| **Home** | `Contracts/collapsible-group/README.md` |
| **Owner** | shared (portfolio) |
| **This product's role** | **consumer**; migration SP-0231, dated plan with deadline 2027-03-31 recorded in the registry's "StreamsPlayer unified UI migration" entry |
| **Function** | Semantic groups: state triangle, whole-header activation, in-flow full-width body, remembered expansion and viewport anchor |

## What this repository must do to stay conformant

The rules are in the home page and are not repeated here. Repo-local facts:

- **Implementation (target):** the `SettingsGroup` `Expander` style in `src/StreamsPlayer.App/App.xaml`. Groups are identified by `SettingsUiProperties.GroupId` and remembered in `settings-ui.json` (SP-0191).
- **Design record:** header packing is not enabled, because every settings group starts expanded and there is no run of collapsed headers to pack. Later choices persist. Single-topic pages are reviewed in phase P2.
- **Not yet conformant.** The Settings groups draw the state triangle (right when collapsed, down when open, mirrored in right-to-left) and the whole header toggles; still open: the per-toggle viewport anchor, touch drag cancellation, the disclosure in `ExchangeDiagnosisWindow`, and the single-topic page review.

A plan is not conformance: this product's adoption row for this contract is `partial` and advances only with a measured result (registry, 2026-10-22).
