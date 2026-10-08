---
name: feedback
description: Durable user corrections, working preferences, and PR template disciplines
type: feedback
---

# User Feedback & Collaboration Rules

## Feedback

- When a task statement is meaningfully ambiguous, ask the user to clarify it
  before choosing an interpretation that could change the expected result.

- **A pull request into someone else's repository uses that repository's PR template verbatim.**
  Fetch `.github/PULL_REQUEST_TEMPLATE.md`, keep its headings, its wording and its checklist exactly
  as written, put an `x` in the boxes that are genuinely true, and add prose only in the section the
  template designates for it. Do **not** write a body of your own structure, and do not paraphrase a
  checklist from memory - the maintainer bots parse the template, so a rewritten one reads as an
  unfilled one. The same applies to the **title**: the template states the required format, and
  inventing a variant gets the PR misclassified.
  The owner has corrected this repeatedly - it is a recurring failure, not a one-off. Concrete cost
  on 2026-07-27: PR microsoft/winget-pkgs#408215 was opened with a hand-written body and the title
  "New version: SerZhyAle.StreamsPlayer version 26.0727.0253" when the template requires
  "Update: Publisher.Name to X.Y.Z". Result: labels `Policy-Test-2.7`, `Validation-Guide` and
  `New-Manifest` - an already-published package classified as a brand-new one - and a validation
  complaint, despite the manifest itself being correct and the Azure pipeline passing.
  This overrides the house rule about the Claude Code footer in PR bodies: that rule is for this
  project's own PRs, and a third-party template wins over it.
  **It happened again on 2026-08-09**, PR #414229, in the same two ways - a self-invented body with
  its own `### Validation` heading, and the `New version:` title.
  **Correction, 2026-08-21: `New-Manifest` was never the tell, and this entry taught that wrongly
  twice.** Checked against the API: every merged submission this package has - #414229, #420274,
  #421532 - carries `New-Manifest` alongside `Validation-Completed`, `Moderator-Approved` and
  `Publish-Pipeline-Succeeded`, and #420274 and #421532 both went out with the *correct* `Update:`
  title. The bot applies it because the pull request adds a manifest folder, which every new version
  does. So the label is noise, the title rule stands on its own merits, and **a symptom that appears
  on the successes too is not evidence of the failure** - which is the general lesson worth more than
  the winget detail. Confirm a diagnosis against the cases that went right before believing it.
  Why the entry did not prevent it, which is the part worth keeping: this file was never opened that
  session. It was grepped for one line and appended to, and the top was never read, so an entry
  sitting at line 12 changed nothing. **Two conclusions.** Read this file before acting, not while
  editing it. And a rule that guards one step belongs at that step as well as here - the winget rule
  now also lives in `winget/README.md` beside "submit a pull request", which is the document actually
  open during a release. Treat a repeat of a recorded correction as evidence the rule is in the wrong
  place, not only as a lapse.
  Also: the template is not stable. On 2026-08-09 it had emoji headings (`## 📖 Description`,
  `## ✅ Checklist`, `## 📦 Manifest Checklist`) and a `This PR only modifies one (1) manifest` box
  that did not exist in July. Fetch it every time; never reproduce a remembered copy.


## Site and documentation pages use the full available width (2026-10-06)

The owner objected that generated HTML (landing, privacy, trust) rendered as a narrow centred column. The contracts already say it: `PAGE-CONTENT` "Layout contract" and `PAGE-STYLE` section 1 - ordinary text starts at the container's left edge and has no arbitrary narrow max-width; only cards and disclosure groups may constrain themselves. The kit's `--wide:1100px` is the contradicting, owner-unresolved value, so a copied cap (here `--wide:1180px` plus 760-860 px text caps) is a defect, not a house style. **Why:** the earlier SP-0192 read of the contracts looked at structure and missed the layout paragraph. **How to apply:** before touching or measuring any page, read the layout paragraph of `PAGE-CONTENT`/`PAGE-STYLE`, and check a new page in a browser at a wide viewport (container width against `innerWidth`), not only the markup.

## The brand backdrop sits behind the text, slow and faint (2026-10-06)

The owner, looking at a site whose hero wave animation ran through the headline and body copy unreadably, stated the rule for a branded animated background: the animation is *behind* the text, the text sits on semi-transparent plates and stays readable, and the motion is slow and pale (faint). This product's site has only the slow, low-opacity blurred glow blobs (`.background-glow`); no wave/particle backdrop exists here.

**Why:** an effect that competes with the copy fails `SITE-EXPERIENCE` rule 8 (contrast of rule 17 behind text) and the owner's own reading of it.

**How to apply:** before adding or restyling a hero backdrop (`WAVE-PARTICLES`), keep it slow, low-opacity and under plates; do not strengthen the existing blobs. Not part of SP-0195.
