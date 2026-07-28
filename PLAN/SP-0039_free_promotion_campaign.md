# SP-0039: Free promotion - make a finished product findable without spending money

**Status:** Draft - the baseline is measured and the channel list is written; nothing has been
executed. Exit: the owner answers the open questions below, and the status becomes Approved.

## Goal

Turn a product that is finished, released on three channels and translated into thirteen languages
into one a stranger can actually find - using only surfaces we already own, PR-based catalogues, and
the author's own writing. No budget, no advertising, no third-party marketing service.

## Why

The distribution problem is solved; the discovery problem has never been touched. Measured on
2026-07-29:

- Four GitHub Releases, **53 ZIP downloads in total** (27 + 18 + 7 + 1, newest last).
- The GitHub repository has **no description, no homepage link, no topics and no stars**
  (`gh api repos/SerZhyAle/StreamsPlayer` returns `description=null`, `homepage=null`, `topics=[]`).
  Description, homepage and topics are the three cheapest discovery fields the platform has, and all
  three are empty.
- The generated site emits a `description`, a canonical URL and hreflang alternates and **nothing
  else** - no Open Graph tags, no Twitter card, no JSON-LD, no `sitemap.xml`, no `robots.txt`
  (`tools/site/templates/_head.html`). A link to it renders as a bare URL everywhere it is shared.
- The social card the site would need **already exists and is unused**:
  `assets/store/social-preview-1280x640.png` is drawn, is not copied into `docs/`, and is referenced by
  nothing.
- winget still serves 26.0723.1040 while two newer versions sit in open pull requests, and the Store
  package for 26.0728.1352 is built but not submitted.

The first two gaps are not a matter of taste. The canon's documentation concept requires every page to
carry Open Graph and Twitter card tags with a real image, a JSON-LD `SoftwareApplication` block, and a
`sitemap.xml` plus `robots.txt` at the site root. Three of those seven requirements are simply missing
here, so part of this ticket is compliance work that happens to be the cheapest promotion available.

So the product is invisible for reasons that cost nothing to fix, and the things that would make it
interesting to a stranger are real and already shipped: a catalog that works on first launch, thirteen
interface languages including right-to-left, no account, no advertising, no telemetry, MIT source, and
RTSP alongside radio and live video. None of that is expressed anywhere a stranger looks.

There is no paid campaign that would outperform simply filling in the free fields first.

## Non-goals

- Do not spend money. No advertising, sponsored posts, paid directories, paid reviews or paid press.
- Do not add telemetry to measure any of this. The privacy promise is a product feature and outranks
  measurement: no analytics script on the site, no tracker in the app, no click-through redirector.
  Measurement uses only counters GitHub and Partner Center already publish.
- Do not astroturf. No second accounts, no upvote requests or vote coordination, no reviews written or
  incentivised by us, no undisclosed authorship, no comments pretending to be an ordinary user.
- Do not nag inside the product. No rate-us prompt, no share button, no notification that exists to
  feed a channel.
- Do not over-claim. The catalog is third-party content that comes and goes; the manual and the site
  were just corrected for exactly this class of defect and must not be re-inflated by promotion copy.
- Do not name another product in Store metadata (policy 10.1.3, `msix/listing/forbidden-terms.txt`),
  and do not use piracy-adjacent vocabulary anywhere, outreach text included.
- Do not reposition the product. The story, the site and the listing stay as they are; this ticket is
  about being found, not about changing what is said.
- Do not invent product features to court an audience.
- Do not treat a publication on someone else's platform as a repository criterion. Submitting,
  posting and listing are owner actions gated by the publishing boundary in `AGENTS.md`; what this
  ticket can prove is the artefact and the record, never the third party's decision.

## Decisions

1. **Owned surfaces before outreach.** Anything that keeps working while the author sleeps - repository
   metadata, page meta tags, sitemap, package manifests, directory entries - is done before anything
   that needs a person to write a post. A post drives a spike; an indexed page compounds.
2. **The language reach is the wedge.** Thirteen interface languages with real right-to-left layout is
   the one claim nothing comparable in this niche makes casually, and the per-language surfaces already
   exist: thirteen site languages, thirteen Store listing languages, per-language search terms.
3. **Release-anchored cadence.** Outreach happens on a release, never as a standing activity: at most
   one post per community per release, and never the same day in every community.
4. **Disclosure is unconditional.** Every post states that the author wrote the application. If a
   community's rule forbids self-promotion, there is no post there - no alternate framing, no proxy.
5. **Binary integrity.** A directory may link to, or mirror unmodified, the GitHub Release ZIP with its
   published SHA256. Any site that wraps the download in its own installer is refused, whatever traffic
   it offers.
6. **Public counters only.** Success is read from per-release asset `download_count`, the GitHub traffic
   API, Partner Center acquisitions, and the merge state of package-manager pull requests. Attribution
   will be coarse; that is the accepted price of decision "no telemetry".
7. **Two audiences, two messages.** The radio/live-video listener and the RTSP camera user are not the
   same person and never share a post.
8. **The forbidden-terms list governs Store metadata, not the world.** Naming alternatives is expected
   on a site like AlternativeTo and allowed there; the Store gate stays untouched.
9. **One demo asset, reused everywhere.** A single short silent screen capture serves the README, the
   site, the Store listing and every post - not one asset per channel.

## Constraints

- One author, evenings. The plan must survive being executed one item at a time, in any order within a
  phase, with weeks of silence in between.
- The site is generated: any meta-tag, sitemap or robots work happens in `tools/site/templates` and the
  copy decks, and `tools/site/build-site.ps1 -Check` must stay green with all thirteen languages
  regenerated. Hand-editing `docs/` is not an option.
- Publishing actions - a release, a tag, winget submission, Partner Center upload, a Pages publish -
  stay gated on explicit owner approval (`AGENTS.md`). This ticket does not loosen that. Note what that
  means in practice here: `.github/workflows/pages.yml` deploys on any push to `main` that touches
  `docs/**`, so every meta-tag or sitemap change **is** a Pages publish and needs its own approval.
- Any number that appears in promotion copy is read from the constant that produces it, never copied
  from an existing sentence. Prose numbers in this repository have been wrong before - the buffer and
  the preview-cache figures were repeated across thirteen languages while the code said otherwise.
- A claim added to the listing multiplies by thirteen, and a Partner Center listing import is
  all-or-nothing per language: one bad cell silently drops that language's whole column and staled the
  export it was built from. Prefer promotion that adds no new claim to the listing at all.
- Screenshots already exist per listing language under `assets/store/`; reuse them rather than build a
  new asset pipeline.
- Repository artefacts stay in English; per-language text only where a deck already exists.
- The app and the site gain no runtime dependency and no outbound request from any of this work.

## Channels

Grouped by what they cost the author, not by expected reach. Every "verified" note below was checked on
2026-07-28/29; everything marked "confirm first" must have its current rules read before acting.

**A. Surfaces we already own (no permission needed, no waiting)**

1. **Repository metadata** - description, homepage, topics, social preview image, README badges. All
   three text fields are currently empty; `gh repo edit` sets them in one command.
2. **Site discovery tags** - Open Graph and Twitter card per page pointing at the social card that is
   already drawn, one JSON-LD `SoftwareApplication` block (name, operating system, category,
   `offers.price = 0`, version, download URL), plus a generated `sitemap.xml` and `robots.txt`.
   Canonical and hreflang are already correct and stay as they are.
3. **The author's own products.** The catalog StreamsPlayer consumes is FastMediaSorter's published
   bank; its users are, definitionally, people who want these streams. The site footer already links
   the portfolio hub at `sza.od.ua`, but the traffic only flows outward - a link back from the sibling
   products' listings and sites is the highest-yield free channel available and costs one edit each.

**B. Package managers and catalogues (free, pull-request based)**

4. **winget** - already published as `SerZhyAle.StreamsPlayer`; the work is keeping versions current,
   not acquiring the channel.
5. **Microsoft Store** - the pending submission is also a discovery surface: thirteen localised listings
   with per-language search terms already written.
6. **Scoop, `extras` bucket** - verified: the main bucket takes only non-GUI tools that are already
   widely used, so `extras` is the correct and achievable target for a GUI application.
7. **Chocolatey community repository** - free with moderation; confirm first (the current submission and
   moderation rules were not readable when this ticket was written).

**C. Software directories (free listings, one form each)**

8. **AlternativeTo** - verified: a free account can add the application, a new account must wait a week
   before its first submission, review takes days, and the author can claim the page by writing to
   support with proof of ownership. Listing it as an alternative to the obvious players is expected
   behaviour there.
9. **Softpedia** - free submission form, PAD file or manual entry; confirm first.
10. **The Portable Freeware Collection** - the release ZIP is genuinely portable, which is the entry
    requirement; confirm first.
11. **FossHub** - free-and-open-source only, which MIT satisfies; the submission route is a contact
    request; confirm first.

Decision 5 applies to this whole group: a site that repackages the download is refused.

**D. One-shot launch moments (spend at most one per release)**

12. **Show HN** - verified rules: it must be something people can run and try without barriers,
    non-trivial, made by the person posting, who stays around to answer; landing pages, reading
    material and minor version bumps are not eligible, and soliciting upvotes is prohibited. A desktop
    player with a portable ZIP qualifies cleanly.
13. **Product Hunt** - verified: launching is free, self-hunting is normal and carries no penalty, and
    asking for upvotes is detected and punished; comments and maker replies weigh more than raw votes.
14. **Reddit** - subreddit rules first, always. Candidates worth reading before posting: general Windows
    software and freeware communities, open-source and side-project communities, and the .NET/C#
    communities for the engineering angle (WPF, LibVLC, thirteen-language localisation with RTL).
15. **A Russian-language write-up** on a platform the author writes natively for - an honest engineering
    post about building and localising the thing, not a press release. The same text can serve a
    Ukrainian audience.
16. **The demo capture** (decision 9): under a minute, silent, showing the catalog, grid previews, a
    language switch into a right-to-left layout, and a video opening. Published once in the repository
    and linked from everywhere.

**E. Compounding, near-zero effort**

17. Answer questions where the application genuinely fits, as a participant who happens to be the
    author - never a drive-by link.
18. Keep release notes written for a reader. Directory editors and package maintainers read them, and
    they are the only "press release" this product will ever have.

## Acceptance criteria

1. `gh api repos/SerZhyAle/StreamsPlayer` returns a non-null description, `homepage` set to the site
   URL, and at least five topics.
2. Every generated page carries Open Graph title/description/image/url/type, a Twitter card, and one
   JSON-LD `SoftwareApplication` block with a zero price and the shipped version; the thirteen-language
   generation is otherwise unchanged and `build-site.ps1 -Check` reports "docs/ is up to date".
3. `docs/sitemap.xml` lists all twenty-six pages, `docs/robots.txt` allows crawling and names the
   sitemap, and both are produced by `build-site.ps1` rather than maintained by hand.
4. A social preview image is set on the repository and the same image resolves at a stable URL for the
   Open Graph tags.
5. One demo capture exists in the repository, is under a minute, has no audio track, and is referenced
   from `README.md` and the generated site. Whether it also becomes a Store trailer is a Partner Center
   action and is recorded, not required here.
6. Each outreach text that will be posted exists in the tree before it is posted - one file per target,
   in English or in the language it will be posted in - so it can be read, corrected and reused rather
   than improvised into a text box.
7. Every attempted channel has one recorded outcome line in this ticket - channel, date, resulting URL
   or pull-request number, current state - including the ones that refused the application, and why.
   The third party's decision is recorded, never claimed as a criterion.
8. Metrics are recorded from public counters at the start and at each later checkpoint. The baseline of
   2026-07-29 is part of this ticket: 53 release-ZIP downloads across four releases, 0 stars, no
   repository topics, no Open Graph tags, winget serving 26.0723.1040.
9. No analytics, tracker, telemetry or redirector was added to the app, the site or the release
   artefacts; every claim on the privacy page remains literally true.
10. Every outreach text names the author as the author, no text exists for a community whose rules
    forbid self-promotion, and no text reuses a term from the forbidden list. Store metadata still
    passes that gate unchanged.

## Risks

- **Download portals that wrap binaries.** Some free-software portals monetise by wrapping installers.
  One such listing does more reputational damage than all the traffic it brings. Decision 5 is absolute.
- **A one-shot moment spent badly.** Show HN and Product Hunt effectively fire once for a product. Both
  reward a polished, complete, immediately runnable thing; firing them on a week with a broken release
  or no demo wastes them permanently.
- **Self-promotion backlash.** The author posts under his own name across every channel. A single
  rule-breaking post is a durable cost, and it is exactly the kind of shortcut a promotion plan invites.
- **Store review risk leaking back in.** The forbidden-terms list exists because this application opens
  third-party stream addresses; promotional prose is the likeliest place for that vocabulary to return.
- **Third-party catalog volatility.** Channels die. Copy that promises a fixed number of working streams
  converts into one-star reviews when reality moves.
- **Coarse measurement.** Without analytics no channel can be attributed with confidence, so the plan
  can only be judged in aggregate. That is a deliberate trade, not an oversight to fix later.
- **Weak locales amplified.** The site's non-English pages carry a machine-translation notice; driving
  traffic to them surfaces translation defects faster than the author can proofread them.
- **The channels already disagree about one word.** `iptv` is a live tag in the winget locale manifest
  while the Store treats the same substring as a build-failing forbidden term and the publishing runbook
  says never to re-add it. Any attempt to unify keywords across channels hits this first, and unifying
  in the wrong direction reintroduces the exact review risk that was deliberately removed.
- **The tagline argues with the product compass.** The canon's compass says the audience is ordinary
  non-technical people and names RTSP as jargon to avoid; RTSP sits in the README tagline and the site
  hero because it is also a genuine differentiator for a smaller, sharper audience. Promotion copy has
  to pick per channel rather than average the two.
- **The promo images are rendered at 2x under 1x names.** `banner-1280x360.png` is 2560x720 and
  `social-preview-1280x640.png` is 2560x1280. A directory that demands an exact pixel size will reject
  them until they are resized, and the file name will not warn anyone.

## Open questions

1. Should the product have its own social account, or do posts stay under the author's existing
   identity?
2. Demo capture: a repository-hosted GIF/MP4 only, or also a video-platform upload?
3. Is Chocolatey worth its packaging and moderation cost once winget and Scoop are in place?
4. Which release does the one-shot moment (Show HN / Product Hunt) attach to - the next one, or a later
   one chosen for being a good story?
5. `iptv` in the winget tags: remove it so every channel says the same thing, or keep it because Store
   policy 10.1.3 does not reach winget and the term is how people there search? This is a positioning
   call, not a cleanup.
6. Does the product get its own domain, or does it stay on `github.io`? A domain would change every
   canonical URL, the sitemap and the Open Graph URLs, so it is cheaper to decide before the SEO work
   than after.
7. The canon's documentation concept expects a "What's new" page and a support page beside the landing
   and privacy pages; this site publishes two of the four, and the repository keeps no changelog. Add
   them, or record the divergence deliberately?
8. Does this ticket get a tactical folder? Phase A is ordinary repository work with static predicates
   and would take one; the outreach half has no predicate a run can check, so it can only close through
   a `BlockNeedUserTest` state. Splitting phase A into `PLAN/SP-0039_free_promotion_campaign/` and
   leaving the rest strategic is the alternative.
