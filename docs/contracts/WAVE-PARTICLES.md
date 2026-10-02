# WAVE-PARTICLES - pointer

| | |
| --- | --- |
| **Id** | `WAVE-PARTICLES` |
| **Version** | 0.14 (draft) |
| **Home** | `Contracts/animated-backdrop/README.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer** - one more implementation of the "particles and lines" backdrop |

## What this repository must do to stay conformant

Draw the animation of the contract's section 4 with the constants of its section 3, on two surfaces:

- **the compact panel** - an audio player backdrop: intent `ambient`, profile `full`, no tuning;
- **the playing station's catalog card or grid tile** - a screen backdrop behind other content: intent
  `decorative`, profile `full`, no tuning, dimmed through `intensity` only (rule 16).

Repo-local: the motion model is `src/StreamsPlayer.Core/WaveParticles*.cs` (constants, session rolls,
clock, the frame as geometry - platform-neutral and unit-tested), and the drawing is
`src/StreamsPlayer.App/WaveParticlesBackdrop*.cs`.

**One recorded exception**, rule 9: Windows "Animation effects" does not stop the motion; the app's own
"Animated background" switch does (owner decision 2026-09-23, registry row with an `until`). Every section 3 value lives once, in
`WaveParticlesConstants`, beside a citation of the contract. A value that differs is an exception row in the
catalog registry, never only a comment here.

Because the contract is below 1.0 it may change shape. The registry row carries the date this product last
verified against it.

**Conformance evidence.** Rung 1 (constants read) is held by `WaveParticlesSessionTests`, which asserts
section 3 value by value and the section 4 pacing at 60, 120 and 144 Hz. Rungs 2 and 3 need the
contract's reference implementation, which does not exist yet.
