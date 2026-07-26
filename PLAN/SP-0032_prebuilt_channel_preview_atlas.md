# SP-0032: Seed grid previews from the prebuilt channel-preview atlas

**Status:** Archived - superseded by SP-0031 (channel preview pictures from the published atlas), which
shipped this capability while this spec was still unbuilt. See
[DONE/SP-0031_channel_preview_atlas.md](DONE/SP-0031_channel_preview_atlas.md).

This file is kept as the record of the product reasoning, not as pending work. Two differences from what
shipped are worth knowing before anyone revisits the area:

- **Trigger.** This spec called for an explicit Settings action, never coupled to a refresh (Decision 2).
  The shipped feature instead offers the download in a bar shown *after* an explicit catalog update.
  Both honour "no download without a user action"; they differ in where the action lives.
- **Unverified criteria.** SP-0031 records AC 4 and AC 6 as still unproven. The closest items here are
  Decision 1 (a local capture must permanently win over an atlas tile) and criterion 6 (a malformed or
  out-of-range tile yields no tile rather than a wrong crop) - the two failure modes most likely to be
  noticed by a user, and the reason the risk list below still has value.

## Goal

Let the user optionally download the upstream channel-preview atlas so that video channels show a
representative frame in grid mode *immediately*, instead of an empty tile that stays empty until
local capture happens to reach that channel.

## Why

Grid mode captures a preview frame locally for a video channel, which is accurate but cold: a
freshly installed catalog is a wall of blank tiles, and capture only fills the ones the user
scrolls to, one connection at a time. Browsing 1800 live TV channels with no visual cue is the
weakest part of the grid experience.

The upstream project already publishes exactly what is missing - a single sprite sheet of
per-channel preview frames with a documented, stable slicing contract and a sidecar mapping each
channel URL to its tile. It covers most video channels in the bank. Consuming it turns the first
run from blank tiles into a browsable wall at the cost of one optional download.

It is published as its **own** versioned asset rather than inside the catalog ZIP, precisely because
it is large (20-50 MB) and has an independent lifecycle. StreamsPlayer should honour that separation.

## Non-goals

- Do not replace or weaken local preview capture. The atlas is a cold-start seed; a locally captured
  frame is the truth and always wins once it exists.
- Do not download the atlas as a side effect of a catalog refresh, at startup, or on any background
  timer. This would violate the explicit-refresh contract.
- Do not make the atlas required. Every existing behaviour must be unchanged when it is absent, and
  the user must be able to remove it and get today's behaviour back.
- Do not use atlas tiles for audio or RTSP channels, or for MANUAL/IMPORTED channels. The upstream
  packer only covers catalog video channels.
- Do not build, publish, or modify the atlas. StreamsPlayer is a consumer of a published artifact and
  must not change the upstream repository.
- Do not persist the decoded tiles as separate per-channel image files, and do not treat the atlas as
  user data to be backed up.
- No new logging facade.

## Decisions

1. **Seed only; local capture wins.** An atlas tile fills a video channel's grid tile only while that
   channel has no locally captured frame. As soon as local capture produces one it takes over
   permanently, and a later atlas update never displaces it.
2. **Explicit opt-in download.** The atlas is fetched only by a deliberate user action in Settings,
   never by a catalog refresh. The action reports progress and can be cancelled; the same action
   updates an already-installed atlas.
3. **Removable.** The user can delete the downloaded atlas and reclaim the disk, returning to
   capture-only behaviour with no other visible change.
4. **Versioned payload, matched pair.** The atlas and its coordinate sidecar are one unit: a tile is
   shown only when both are present and consistent. A revision published under a new suffix is a new
   payload, not an in-place mutation of the installed one - an installed older payload keeps working
   until the user updates.
5. **Keyed by channel URL.** Tiles resolve through the sidecar's URL key, the same stable identity the
   catalog and the favicon sidecar already use. A channel absent from the sidecar simply has no tile.
6. **Defensive slicing.** A tile ordinal that falls outside the sheet, a non-integer sidecar value, or
   a payload that fails to decode yields *no tile* - never a wrong crop, a stretched image, or an error
   dialog. A malformed download must leave the previously installed payload intact.
7. **Size-bounded and interruptible.** The download is bounded by a documented maximum and a timeout,
   is written so an interrupted or failed transfer cannot leave a half-installed payload in use, and
   never blocks the UI.
8. **Localized in all shipped languages** - English, Russian, and Ukrainian - with no emoji.

## Constraints

- The slicing geometry is a shared invariant with the offline packer. It must be read from, or
  validated against, the published contract rather than assumed; a geometry mismatch must degrade to
  "no tiles" rather than render drifted crops.
- Memory: the sheet is large. It must not be held decoded per tile or duplicated per visible channel,
  and grid scrolling performance must not regress measurably against today's capture-only grid.
- The download and its storage live alongside the existing local state; the payload is a cache, not
  state the user would miss, and a wiped local folder must self-heal into capture-only behaviour.
- Catalog contracts and slicing stay platform-neutral; image decode and grid rendering stay in the app.
- Explicit catalog refresh and the MANUAL/IMPORTED merge protection are unchanged.

## Acceptance criteria

1. With no atlas installed, grid mode behaves exactly as it does today.
2. The Settings action downloads the atlas and its sidecar, shows progress, and can be cancelled
   without leaving a partial payload installed or the previous payload damaged.
3. After installation, a catalog video channel with a tile and no local capture shows its preview frame
   immediately in grid mode, at every tile size.
4. Once a channel has a locally captured frame, the captured frame is shown and a subsequent atlas
   install or update never replaces it.
5. Audio, RTSP, MANUAL, and IMPORTED channels never take a tile from the atlas.
6. A channel absent from the sidecar, with a non-integer value, or with an out-of-range ordinal shows
   no tile and no error.
7. Removing the atlas through Settings frees the disk and restores capture-only behaviour.
8. A corrupt, truncated, or non-decodable download is rejected, leaves any previously installed payload
   intact, and reports a clear failure.
9. A catalog refresh never downloads, updates, or deletes the atlas.
10. All new user-facing strings exist in English, Russian, and Ukrainian.
11. Build and tests pass; slicing arithmetic and the defensive cases are covered by tests, and the grid
    seeding, capture-wins, update, and removal flows are confirmed by run-and-observe evidence recorded
    as `expected: ... | actual: ...`.

## Risks

- **Staleness.** A curated frame can be months old and may not reflect current programming. Decision 1
  bounds the exposure: the tile is a first-impression placeholder and is replaced by real capture.
- **Geometry drift.** Changing the tile grid on one side without the other misaligns every crop.
  Constraint 1 and Decision 6 make a mismatch degrade to no tiles rather than to wrong images.
- **Download weight.** 20-50 MB is significant on a metered connection. Decision 2 keeps it strictly
  opt-in and separate from refresh.
- **Memory pressure.** A large sheet handled naively could regress grid scrolling; this needs explicit
  measurement against the current grid, not an assumption.
- **Coverage gap.** Not every video channel has a tile - channels that did not answer during the
  upstream capture pass have none - so the grid stays partially blank even after install. This is
  expected, not a defect.

## Open questions

None. Seed-only semantics and the explicit opt-in download were settled with the owner.
