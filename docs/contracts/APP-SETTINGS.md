# APP-SETTINGS - pointer

| | |
| --- | --- |
| **Id** | `APP-SETTINGS` |
| **Version** | 0.3 (draft) |
| **Home** | `Contracts/desktop-app-ux/README.md`, with the rules in `APP-SETTINGS.md` |
| **Owner** | CyrFlip |
| **This product's role** | **consumer** (opted in by SP-0188, 2026-10-02) |

## What this repository must do to stay conformant

The twelve rules are in the home page and are not repeated here. Repo-local facts:

- **Rule 1: One instance, found where the user is, last page restored.** Single modal Settings window opened from the header gear. Restores the last active page within the running session, defaulting to General on application start.
- **Rule 2: Vertical page list, About last.** Six pages: General, Library, Playback, Audio, Files, About.
- **Rule 3: Page anatomy.** Description header on every page; each setting has caption, control, and visible muted hint.
- **Rule 4: Commit model.** Second model: no Save/Cancel buttons; values commit immediately on touch; Close is the sole exit (`IsCancel="True"`); irreversible actions have explicit buttons with confirmations defaulting to the safe answer (`APP-BEHAVIOUR` rule 5).
- **Rule 5: Language selector.** Located on the General page with the globe glyph (`Icon.pending.app.language`), endonyms only, live switch that re-renders all open windows and flips RTL layout without closing.
- **Rule 6: Theme.** System / Light / Dark selector on the General page. High-contrast mode has a dated exception recorded in `_meta/REGISTRY.md` (expires 2027-09-30).
- **Rule 7 & 8: Scaling & Sizing.** Derives sizes from font and content; hit targets comply with pointer floors; tested from 100% to 200% scale.
- **Rule 10: Accessibility.** Sensible tab order, visible focus indicators, automation names for all glyph controls and page tabs.
- **Rule 12: About page.** Shows version, MIT licence link, privacy link, author, and user-initiated diagnostic bundle export button.

**0.3, read 2026-10-07 (SP-0206).** Section 9 (an irreversible operation on a surface with no Commit sits behind a dedicated
button with `APP-BEHAVIOUR` rule 5's confirmation, never on a value control) is how the Settings window already works: the
operation buttons are in `SettingsWindow.xaml.cs` and their confirmations default to the safe answer. Gate:
`DesktopUxConformanceTests` (its handler list names three of the operation buttons; widening it to every one is optional).
