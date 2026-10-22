# BUTTONS - pointer

| | |
| --- | --- |
| **Id** | `BUTTONS` |
| **Version** | 2.0 (active), read 2026-10-22 for SP-0231 |
| **Home** | `Contracts/buttons/README.md` |
| **Owner** | shared (portfolio) |
| **This product's role** | **consumer**; migration SP-0231, dated plan with deadline 2027-03-31 recorded in the registry's "StreamsPlayer unified UI migration" entry |
| **Function** | One mandatory command system: content-fit widths, wrapping rows, equal row heights, neutral dismissal, answer order, markers and semantics |

## What this repository must do to stay conformant

The rules are in the home page and are not repeated here. Repo-local facts:

- **Implementation (target):** the base `Button`, `GlyphButton` and answer styles in `src/StreamsPlayer.App/App.xaml`, read through the `Ui*` tokens.
- **Not yet conformant.** The inventory found work in every window: answer order is wrong in seven dialogs, the dismissal is red-tinted and `AnswerButtonConformanceTests` requires it, the main title line shrinks in a `Viewbox`, and the grid-tile chips have literal sizes. The ordered work is phases P3 and P4 of SP-0231. The red-dismissal rule of SP-0218 is superseded by `BUTTONS` section 3 rule 5 when P3 lands.

A plan is not conformance: this product's adoption row for this contract stays `-` until a measured result exists.
