# APP-BEHAVIOUR - pointer

| | |
| --- | --- |
| **Id** | `APP-BEHAVIOUR` |
| **Version** | 0.10 (draft - it may change shape until every consumer has confirmed it) |
| **Home** | `Contracts/desktop-app-ux/README.md`, with the evidence per rule in `APP-BEHAVIOUR.md` |
| **Owner** | **this product**. StreamsPlayer writes the amendments and answers the questions |
| **This product's role** | **producer and consumer** - the rules were read from this code, and this code is bound by them |

## What this repository must do to stay conformant

The twelve rules are in the home page and are not repeated here. Two things are repo-local:

**It owns them.** A change to what a rule says is made in the contract first and in this code second
(`Contracts/_meta/RULES.md` section 6). A finding in another product is an amendment written here, not a
private fix there.

**It is bound by them.** The two deviations this product declared against itself on 2026-09-22 were
closed on 2026-09-23 by SP-0109, and both closures are recorded in the store's `_meta/REGISTRY.md`:

- rule 12 - the settings window holds values only; every operation lives in the Close-only Tools window
  (Operations menu) or, for one channel, in that channel's menu;
- rule 6 - no site shows an exception's text; the user gets a cause read from the exception's type and an
  action, and the exception goes to the log.

Keep it that way when adding a control: an operation goes in `ToolsWindow`, never in `SettingsWindow`, and
a failure message is built with `FailureCauseText`, never from `exception.Message`.

**No open deviations.** The two the contract sync found on 2026-09-24 were closed the same day by
`PLAN/SP-0114`, and the closures are in the store's registry:

- rule 5 - clearing an empty listening history says there is nothing to clear;
- rule 9 - `GlyphAutomationNameTests` gates every glyph-only control, combo box and slider, and a button
  that swaps its style at run time must re-point its name by resource.

Keep it that way when adding a control: a glyph-only one names itself with `{DynamicResource Key}`, and
code that changes its role uses `SetResourceReference` on `AutomationProperties.NameProperty`, never
`AutomationProperties.SetName` with a string.

**Rule 1 binds dialogs, not companion surfaces** (0.10). `PlayerWindow` and `CompactPanelWindow` stay
open beside the main window and are non-modal by design; every window that asks, collects or confirms is
modal, owned, centred on its owner, with one `IsCancel` exit.

**Before changing a window.** Rules 1, 2, 3, 5, 6, 10, 11 and 12 all constrain what a dialog or a long
operation may do. `PLAN/SP-0102` is this product's own interface audit and is the ticket that mapped them.

**Conformance evidence.** No vectors. Five rules are gated in CI - `LocalizationParityTests`
(rule 8), `LocalizedCallSiteTests` (rule 7), `TabAutomationNameTests` and `GlyphAutomationNameTests`
(rule 9), and
`DesktopUxConformanceTests` (rule 12's handler allow-list with rule 1's Escape on the settings window, and
rule 6's scan for exception text outside a log call). The rest are read
against the code, and section 13 of the contract names the ladder for turning them into gates.
