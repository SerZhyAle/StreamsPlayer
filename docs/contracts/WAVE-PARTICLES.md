# WAVE-PARTICLES - pointer

| | |
| --- | --- |
| **Id** | `WAVE-PARTICLES` |
| **Version** | 0.15 (draft) |
| **Home** | `Contracts/animated-backdrop/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer** - one more implementation of the "particles and lines" backdrop |

## What this repository must do to stay conformant

Draw the animation of the contract's section 4 with the constants of its section 3, on three surfaces:

- **the compact panel** - an audio player backdrop: intent `ambient`, profile `full`, no tuning;
- **the playing station's catalog card or grid tile** - a screen backdrop behind other content: intent
  `decorative`, profile `full`, no tuning, dimmed through `intensity` only (rule 16).
- **the product site** - one fixed canvas behind every generated page, running the contract's reference
  implementation `docs/assets/wave-particles.js` byte-identical (pinned in `tools/site/kit-provenance.txt`): intent
  `decorative`, palette `GREEN`, its `intensity` 0.3 and `speed` 0.5 fixed by the surface, which section 5 has allowed a
  decorative backdrop behind text since 0.15; every block of text stands on a semi-transparent plate.

Repo-local: the motion model is `src/StreamsPlayer.Core/WaveParticles*.cs` (constants, session rolls,
clock, the frame as geometry - platform-neutral and unit-tested), and the drawing is
`src/StreamsPlayer.App/WaveParticlesBackdrop*.cs`.

Rule 9 is held as 0.14 decided: Windows "Animation effects" stops the motion into the settled still frame
(`BackdropEnvironment.AnimationEffectsEnabled`, drawn by `EnsureStill`), and the app's own "Animated background" switch can
only add stillness. The dated exception recorded against the rule on 2026-09-23 (owner decision, registry row) was closed by
SP-0186 on 2026-10-02. Open under SP-0210, on an owner ruling: whether the own switch's off - which removes the backdrop
instead of holding a frame - satisfies the rule. Every section 3 value lives once, in `WaveParticlesConstants`, beside a
citation of the contract. A value that differs is an exception row in the catalog registry, never only a comment here.

Because the contract is below 1.0 it may change shape. The registry row carries the date this product last
verified against it.

**Conformance evidence.** Rung 1 (constants read) is held by `WaveParticlesSessionTests`, which asserts
section 3 value by value and the section 4 pacing at 60, 120 and 144 Hz. Rungs 2 and 3 need the
contract's reference implementation, which does not exist yet.

**0.15, read 2026-10-07 (SP-0206).** Section 5 now lets a decorative backdrop behind text fix `intensity` and `speed` itself and
offer no control: the site's `data-intensity="0.3" data-speed="0.5"` is that shape, so the site needs no exception row for it.
The reference implementation and the rung 2 byte identity are held by `tools/site/build-site.ps1 -Check`.

**Update, 2026-10-07 (SP-0210).** Rule 10's cloaked-window clause is now held in the tree: `WindowCloaking.IsCloaked` reads
`DWMWA_CLOAKED` and the backdrop's tick advances nothing and draws nothing while the window is cloaked (not yet observed on a
second virtual desktop). Open: whether removing the backdrop when the user's switch is off satisfies rule 9 as 0.14 decided.
