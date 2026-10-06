# WINDOWS-UI - pointer

| | |
| --- | --- |
| **Id** | `WINDOWS-UI` |
| **Version** | 0.1 (draft) |
| **Home** | `Contracts/desktop-app-ux/WINDOWS-UI.md` |
| **Owner** | Fast Media Sorter & Sharing for Windows (the reference viewer); portfolio-wide opt-in decided 2026-10-05 |
| **This product's role** | **consumer** (opted in by the same portfolio decision; adopted and audited by SP-0191, 2026-10-05) |

## What this repository must do to stay conformant

The profile supplements `APP-BEHAVIOUR`, `APP-STYLE` and `APP-SETTINGS`; this product owns the first
and second of those, so a conflict is an amendment written in the store first, never a local deviation.

SP-0191 adoption notes, per section:

- **2 Navigation** - the settings window's vertical page list carries a glyph and a localized caption;
  the selected row has the accent bar at the reading start (mirrored for right-to-left languages) over
  a soft selected fill; hover, keyboard focus and selection are distinguishable; About is last. Each
  destination's glyph takes its identity ink from ThemeService's NavInk* table (ICON-RENDER 0.16
  section 11; every pair gated at 3:1 against the nav fills in both themes), and the column carries
  the product name above and the version below.
- **3 Groups, discovery and remembered context** - pages hold independent collapsible groups drawn as
  the reference draws them: a full-width soft bar carries the disclosure glyph and the bold caption;
  collapse changes visibility only and hands focus to the header; the last page, each group's
  expansion and each page's viewport anchor (stable group ID plus offset into it) persist in
  `settings-ui.json` and survive a fresh process. Search is reachable from the header and the
  keyboard (section 3.5): it matches localized captions and descriptions, each result identifies the
  setting and its page and group, and activating one selects the page, expands the owning group,
  scrolls it into view and focuses the editor. Group value summaries are omitted as duplicating the
  settings directly beneath each header.
- **4 Rows and editors** - caption and muted description in the leading column, editor in the trailing
  column of the same row, editors aligned to one shared page column capped inside the reference band;
  boolean rows put the checkbox at the reading start with the description beneath, in every window
  including `AddStreamWindow`. The product has no numeric stepper, slider or password editor, so the
  corresponding rows do not arise.
- **5 Typography and DPI** - system font, hierarchy of page title, group caption, setting caption and
  muted hint; WPF's per-monitor scaling with the palette-derived metrics; hit targets are the shared
  glyph-button metrics (16 px glyph in a 30 px+ target, above the 28 px mouse floor). Touch input does
  not arise in this product; the 44 px floor is recorded as not applicable rather than verified.
- **6 Theme** - system/light/dark live through the existing `ThemeService`; navigation identity inks
  are adopted under `ICON-RENDER` section 11 with the one-table light/dark rule and the contrast
  gate. High contrast remains the dated exception recorded 2026-10-02 (expires 2027-09-30).
- **7 Secondary windows** - unchanged; the dialog set already holds `APP-BEHAVIOUR` rule 1.
- **8 Viewer** - the control panel keeps its bottom anchoring and 4 s idle hide, with a new pin
  (global keep-visible preference, glyph shows the toggle state). The overlay is hosted inside the
  backend's native video surface (SP-0072), which keeps it out of every UI Automation tree; its
  buttons therefore could not be driven by the tooling in this environment, and the pin's
  run-and-observe acceptance is recorded for the owner's manual pass (SP-0191).
