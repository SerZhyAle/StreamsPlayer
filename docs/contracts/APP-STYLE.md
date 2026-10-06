# APP-STYLE - pointer

| | |
| --- | --- |
| **Id** | `APP-STYLE` |
| **Version** | 0.12 (draft) |
| **Home** | `Contracts/desktop-app-ux/README.md`, with the detail in `APP-STYLE.md` |
| **Owner** | **this product** |
| **This product's role** | **producer and consumer** |

## What this repository must do to stay conformant

Five rules bind: three theme modes applied without a restart and following the system while running; one
named palette table with every reference resolved dynamically; the role vocabulary of section 4, which is
this product's own palette with the names generalized; a surface held out of the theme declaring itself
and carrying its own text colour (the player's video overlay); one flat glyph dictionary with one meaning
per shape.

**Where this product stands (2026-09-24).** All five rules hold; there are no open deviations. The
exception the contract sync recorded against sections 3 and 5 was closed the same day by `PLAN/SP-0114`:
the accent captions take `AccentTextBrush` (and the glyph button's caption now binds the button's own
foreground, so that ink actually reaches it), the status dots and the stop-recording glyph take the
palette roles `SuccessBrush` / `WarningBrush` / `DangerBrush`, and every other literal colour sits under a
declaration where it is defined.

**Keep it that way.** A literal colour is legal in exactly two places, and `ThemeColourDeclarationTests`
fails the build on any other: the palette table (`ThemeService` plus its design-time defaults in
`App.xaml`, which must name the same roles), or under a comment containing `Out of theme (APP-STYLE 5`
with the reason - in markup right before the element, an ancestor, or a style it is based on, or as a
window's first child; in code anywhere in the file.

**Section 5 lets a whole window be content-coloured** (0.10): declared where it is defined, its chrome
still themed, anything drawn over the content carrying its own contrast. `PlayerWindow` - black behind
video - is that shape and has to declare itself.

**The rule that is easiest to break here is the dynamic one.** A themed brush referenced statically keeps
working and silently stops following the theme. `ThemeService` swaps brushes in the application's resource
scope, so a static reference removes that element from the theme permanently.

**What this product does not have**, and the contract marks proposed rather than required, so none of it
is a deviation: no shared font, size or spacing constant; no contrast measurement; nothing for high
contrast mode or the system text scale. The `danger`, `warning` and `success` roles now exist here and are
reported in the contract's section 4; they colour state marks, not the confirmation dialog, which is the
system message box.

**Conformance evidence.** No artifact in the store. Rungs 1 and 2 of the contract's section 7 run in CI in
`ThemeColourDeclarationTests`: no palette key referenced statically in markup or read by lookup in code,
and the code table and its markup defaults naming the same roles.
