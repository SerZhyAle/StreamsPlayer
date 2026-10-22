# UI-SIZING - pointer

| | |
| --- | --- |
| **Id** | `UI-SIZING` |
| **Version** | 1.1 (active), read 2026-10-22 for SP-0231 |
| **Home** | `Contracts/ui-sizing/README.md` |
| **Owner** | shared (portfolio) |
| **This product's role** | **consumer**; migration SP-0231, dated plan with deadline 2027-03-31 recorded in the registry's "StreamsPlayer unified UI migration" entry |
| **Function** | Shared size and input profiles: a stable pointer or touch profile, interface scale, text enlargement kept apart, declared and tested smallest targets |

## What this repository must do to stay conformant

The rules are in the home page and are not repeated here. Repo-local facts:

- **Implementation:** `UiProfile`, `SizeScales`, `UiMetricsResolver` and `UiProfileStore` in `StreamsPlayer.Core` (the one token table; no component carries a size literal), `UiProfileService` in `StreamsPlayer.App` (publishes the `Ui*` resources), and the two selectors in Settings, General.
- **Profile record (permanent variants):** pointer is the default and is chosen from `ui-profile.json` when the process starts, never from an input event. The sizes Compact, Standard, Large and Very large are the factors 0.75, 1, 1.25 and 1.5, and any stored factor is clamped to 0.75 .. 2.0. The declared smallest targets are 24 (pointer) and 36 (touch), proven in `UiMetricsTests`. The full record is published in phase P6 of the migration.
- **Not yet conformant.** Only Settings, General renders from the tokens; every other surface adopts as its phase lands. Measured so far (2026-10-22): the group-header and checkbox targets at every size step and at the touch profile, the window refit, and the restored choice. Not yet: the operating system's text-size input, a touch device, and every other surface.

A plan is not conformance: this product's adoption row for this contract is `partial` and advances only with a measured result (registry, 2026-10-22).
