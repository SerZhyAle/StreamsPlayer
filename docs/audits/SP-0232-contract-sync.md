# Accepted-contract synchronization, 2026-10-22

This is the product's evidence record for SP-0232, authorized on 2026-10-22. It audits the accepted
2026-10-17 consolidation and later applicable amendments against the working tree. It does not
certify every existing surface, authorize a publication, or close the earlier implementation tickets.
Contract homes are resolved through [the agent-rules pointer](../../CLAUDE.md); no contract is copied here.

The scope is the Windows product, its installer/portable/Store delivery of the same application,
all shipped interface dictionaries, its generated official site, offline help and its own shared-portal
contributions. Android, watches, WebSocket/browser clients, exchange servers, resource publication,
clipboard exchange and operator administration are not implemented by this product.

The complete local receipt set is `temp/SP-0232/`: the before-status and registry snapshot,
`inventory.json` (initial snapshot), `inventory-current.json` (29 closing editions and SHA-256), command logs, TRX results,
`browser-results.json` and screenshots. PLAN and temporary receipts are untracked; the conclusions
and exact limitations needed by a clone are recorded here rather than depending on those files.

## Clause-to-surface audit

"Retained" means an earlier implementation claim was not upgraded. "Partial" identifies evidence
that does not cover the whole contract. Dates below are read/audit dates, not new runtime certificates.

| Contract, current edition | Clauses and actual surface | Outcome and remaining owner |
| --- | --- | --- |
| APP-ACTIVATION 0.13 | Rules 2-5, section 7 B, 2026-10-17 profiles: `SingleInstanceIdentity`, `ActivationMessage`, `ActivationPipe`, `ForegroundActivation`, `MainWindow.Launch` | CLI + acknowledged current-user pipe; frozen session/profile names retained. URI registration, window-message and one-shot profiles not used. Names exception is superseded by frozen-anchor law; cross-user/session, hung-window and real second-start acceptance still owed to SP-0239. Retain 0.11. |
| APP-STYLE 0.13 | Sections 2-5, section 10, consolidation's conditional status fills: `ThemeService`, `App.xaml`, `ThemeColourDeclarationTests` | Dynamic palette and declaration gates retained. No state-filled result panel exists; state dots/ink and answer tints do not establish that scope. Do not invent five unused fills. Whole-window rendered/theme/contrast acceptance remains SP-0231; retain 0.12. |
| BOOLEAN-OPTION 2.0 | Sections 2-5: `BooleanOptionRow`, Settings General, Add stream and Broadcast audio | SP-0231 P1 is partial; remaining options, local-help activation, touch cancellation and text scaling are its P2/P6. Deadline 2027-03-31 unchanged. |
| BUTTONS 2.0 | Sections 2-6, UI-SIZING precedence: `App.xaml` shared/answer styles, dialogs, Main, Player, compact strip, site controls | Missing command semantics, neutral dismissal, wrapping and measured targets remain SP-0231 P3-P5. System-owned MessageBox rendering is explicitly allowed; the old no-glyph exception is closed for that scope only. Coloured ordinary commands and unmeasured compact chips remain migration debt. |
| CAPTURE-OUTPUT 0.6 | Rules 1-12, 16 and consolidation: `CaptureFileName`, `CaptureFolders`, `CapturedFrameWriter`, recording finishers | JPEG frames and source-container recordings only. No plain-text/PNG capture, so the new no-metadata rule is not applicable. Filename/folder tests retain earlier proof; new container-time measurements on both engines are SP-0239. Retain 0.5. |
| CHECK-VERDICT 0.13 | Rules 1-3, 9, 11-14; count amendment and 2026-10-18 clarification: `scripts/test.ps1`, `TestResultSummary.ps1`, `check.ps1`, build and CI/release test entry points | Structured TRX counts now refuse zero, skipped, missing and malformed suites and distinguish failure from missing proof. New writer uses `run=N skipped=M`. The aggregator counts checks, not tests. Other legacy script paths are not certified by this fix; SP-0239 owns their audit. Partial adoption only. |
| COLLAPSIBLE-GROUP 1.1 | Sections 2-5: SettingsGroup, settings context and diagnosis disclosure | SP-0231 owns the existing partial state triangle, single-topic review, anchoring and pointer/touch behavior; no second implementation is introduced. |
| CONTEXT-HELP 0.1 | Rules 1-3, 7-19, conformance rungs 1-3: help map, resolver, page generator and help baseline | SP-0230 remains partial. S1 source/generator gates can pass with accepted baseline debt; they do not prove screenshots, all locales or the complete F1/button walk. No baseline line is added by this task. |
| DEVICE-EXCHANGE 1.7 | 2026-10-17 format-unsupported and generated corpora; section 28; section 29 DS-DU and section 30 DV: source service, account state, producer and independent incoming windows | SP-0228's partial 1.5 implementation and SP-0237's security/transport exceptions retained. Native roles only; WEB-BINDING, REMOTE-ADMIN, clipboard and server-only corpora not applicable. DS maps to the one member account with no separate outgoing selector; DT/DU announcement, online/guest transitions and server/state display need SP-0239 proof/implementation. No 1.6/1.7 conformance claimed. DV requires verifiers on every announcement; the current producer omits them. R/DV password work is SP-0239 item 10 with SP-0237 item 9. |
| DIAGNOSTIC-REPORT 0.14 | Rule 3, section 8 C and canonical omission amendment: `CurrentLog`, `DiagnosticValueRedactor`, `DiagnosticArchiveBuilder` | Synthetic structured PIN, query, connection-string and multiline private-key cases are sanitized at the sink/export; oversized untrusted logs contain only the ordered canonical marker. The unsafe raw head/tail path is removed. Retained source logs stay untouched and other archive entries survive. Local evidence, not cross-product evidence. |
| DOC-EXTERNAL-QUALITY 0.12 | Section 3; section 8 A-E; section 9 F-I; locale consolidation: help and `docs/portal/streamsplayer*.md` | Newly scoped partial reader/contributor: EN/RU/UK portal contributions belong to this product, portal hosting belongs to its owner. Offline help follows CONTEXT-HELP/PAGE-CONTENT exceptions. Screenshots, glossary/index and freshness coverage need SP-0230/SP-0239. The former blanket "not adopted because only a landing exists" is withdrawn. |
| DOC-INTERNAL-QUALITY 0.10 | Rules 1-7 and local-corpus amendment: root registry and `check-docs.ps1` | Existing tracked source/render registry layout retained as a documented permanent layout variant. Ignored PLAN/temp files are not published corpus entries; there is no `dev/` corpus or dev-log writer. Portal contribution sources now declared. No inaccessible ticket links are added to tracked documents. |
| HARNESS-PROFILE 0.12 | Consolidation's shape-1/3 path rule; shape 4 unchanged | `.sza-canon.json` declares shape 4, no ledger and no dev-log/profile override. New shape-1/3 writer cases are not applicable; retain 0.11 rather than claim to test canon tooling. |
| ICON-RENDER 0.24 | Sections 19-21: app controls, state triangles, required versus optional glyphs | Current BUTTONS/UI-SIZING take precedence. Fixed-direction server-relay path now declares LTR in Settings; other target/contrast/runtime gaps stay with SP-0231 and its existing deadline. Partial, not a blanket icon certificate. |
| ICON-SET 0.36 | 2026-10-17 help names/paste geometry; 2026-10-19 tour and paste first use: map, vendored glyphs and generated XAML | Consumer map updated through the existing generator: 63 canonical meanings, one pending Unhide and nine private entries; 71 XAML geometries and 64 README copies. Canonical drawings are copied unchanged. No tour is added. Geometry/name/RTL/catalog-byte checks are required. Qualified RU/UK More-actions and pending Unhide exceptions are retained for SP-0239 review. |
| INPUT-PARITY 0.4 | Consolidation and VIEWER-KEYBOARD; `PlayerWindow.Window_KeyDown`, main SMTC | Scoped live-player audit: no file movement, photo rotation, slideshow or seekable-file collection; those defaults are not invented. F11/R and SMTC remain; F fullscreen and supported mute/volume/default/editor isolation need SP-0239. Earlier pointer's unsupported seek/back claims are withdrawn. |
| LAN-DISCOVERY 0.11 | Scope, rule 1 and consolidation: imported broadcast description/explicit endpoint connections | Not an announcer or mDNS browser. No DNS-SD code/dependency exists; reserved broadcast services are not a feature. The previous consumer claim is withdrawn, including the wrong SFTP service spelling. Descriptor fallback belongs to LIVE-BROADCAST. |
| LIVE-BROADCAST 1.3 | Higher-schema wording amendment; concurrent reception; 2026-10-22 item R: descriptor parser, incoming slots and transports | Existing partial 1.2 retained, including canonical refusal tests. Per-window cancellation is locally tested; protected/sealed joint replay, source loss, stalled siblings, 4-to-8 acceptance and both engines remain SP-0228/SP-0237. Nothing is inferred from a build. R now permits no auth only after explicit none choice; no current password-choice profile exists, assigned to SP-0239 item 10. Receivers/schema/vectors are unchanged. |
| MEDIA-CLASSIFICATION 0.12 | Sections 1-4 and scope amendment; `MediaKind`, `StreamMediaKindClassifier` | No eight-category primary-media classifier: Audio/Video/Rtsp select a stream engine under STREAM-BANK rule 4. A .m3u8's playback route is not its primary-category STREAM label. Prior whole-contract claim withdrawn; proposal asks owner to clarify this Windows scope. No .jxl decoder/classification feature is invented. |
| PAGE-CONTENT 1.5 | 2026-10-17 footer/help scope and locale rules: site generator, help templates, portal contribution | Existing generated landing/child/footer structure retained; support/history outcomes below and source-copy facts are gated. Offline help needs no site grid, remote fonts or download hero. Rendered acceptance is a risk-selected sample, not all pages. Partial. |
| PAGE-STYLE 3.4 | Query precedence, code ink, glyph chrome; section 14 kit/components: `_head.html`, site.js, served CSS | Both query spellings now work on all generated locale/child pages; invalid values cannot select an unshipped locale, canonical valid value wins, other query values/anchors survive. Existing kit 1.6 remains explicitly behind the 3.4 reference; SP-0231 P5 owns re-vendoring and component migration. Old-byte provenance is honest, not current compliance. |
| SETTINGS-NAVIGATION 2.0 | Sections 2-5: rail, About and page timings | SP-0231 P2 owns tokens, selected semantics, narrow adaptation, complete About set and measured timings. No timing or screen-reader claim is added. |
| SITE-EXPERIENCE 0.4 | Page-tier rules, 2026-10-17 support/history amendment: generator and `ReleaseNotes.ps1` | Support and static shipped-release history are now permitted contract shapes. Newest-first records remain the single source; latest-download resolver stays runtime. Kit/rendering exceptions are kept under SP-0231. Publication/live-host outcome is unverified. |
| SITE-FAMILY-MAP 1.9 | Section 2 tool names, section 2a shared pages, section 3 contact/footer | Windows sibling footer name corrected to Fast Media Sorter & Sharing for Windows. Shared portal links use registered routes, not an extra product card. Help footer exemption applies. Shared server-page mark work belongs to SP-0234; no new server entry is rendered here. No other product/map row is changed. |
| SITE-STRUCTURE 0.3 | Section 2, rules 2/8/10/11 and support amendment: 65 localized pages plus root 404 | Conditional support exists in 13 locales, footer links and release-note generation already have structural/negative gates. Superseded type/static-version exceptions close; the original published-history check remains explicitly open until an authorized publication. |
| STREAM-BANK 2.4 | Rules 1-14, amendments L/O and 2026-10-17 consumer safety: parser, bank reader, merger, playlist import, artwork and retry | Existing tests cover named columns, missing/default/unknown media kind, bounded input/indices, HLS refusal, cancellation and user-row protection. Higher artwork schema is already refused before fetch; stale exception can close after fresh tests. Language/country closed-set normalization remains a real gap (raw parser fields, permissive country fallback); SP-0239, due 2026-12-31. Producer source repair is outside this repo. |
| UI-SIZING 1.1 | Sections 2-5: profile/store/token resolver, General and later surfaces | SP-0231 P1 measured scope retained. Final-width remeasurement, remaining surfaces and complete text/DPI/touch matrix remain its explicit milestones. No implicit deadline renewal. |
| UPDATE-MANIFEST 0.12 | Scope, version amendment: ProductInfo and site's Releases link resolver | Declared, not bound: no manifest producer/client. Releases link browsing does not implement cache/hash/version comparison. No Android versionCode or remap parser added. |
| WINDOWS-STORE 0.2 | Rule 9, conformance rungs 1-4 and 2026-10-17 cap/BOM/import amendment: listing builder and MSIX read-back | Builder rejects 22+ words across actual merged cells before writing; 21 allowed; BOM/no-BOM no-change round trips preserved. New writes retain no-BOM dialect. Local package/listing fixtures do not prove new live import/re-export acceptance; SP-0239 carries that evidence at the next authorized submission. Anchors untouched. |

## Compatibility and data protection

| Boundary | Evidence and disposition |
| --- | --- |
| Names and optional fields | CSV/header-name parsing, tolerant stored enums, named Store columns and existing exchange replay tests remain in place. New Store checks count the actual retained cells, not only replacement terms. Unknown Store fields survive the no-change round trip. |
| Higher schemas | Activation refuses unknown commands/schema; artwork `EnsureSupported` precedes fetch; exchange and broadcast unsupported-version outcomes remain local canonical refusals. No unrecognized security carrier is half-imported by a new path. Remaining sealed fallback defects are SP-0237 release blockers. |
| Absence and defaults | Missing catalog columns remain optional; blank is-live remains unstated; unknown media kind falls back to routing. No UI profile, user channel, saved selection or shortcut identity is migrated by this run. LAN/primary-classifier scope corrections change claims only. |
| Bounds and cancellation | Existing 32 MiB playlist/bank-body ceilings, atlas pixel/byte bounds, integer favicon parsing and cancellable publish retry remain. New diagnostic export never reads an oversized log tail; exact-ceiling logs are sanitized whole. |
| UI defaults/remaps | No shared-default migration is silently applied. Native system-dialog presentation is supported; old product-owned dismissal/colour/compact differences stay in SP-0231's migration. |
| Generated artifacts | Glyphs use Sync-IconGlyphs; site HTML uses build-site. No canonical vector, atlas, CSS or icon path is hand-edited. The current CSS reference is deliberately not substituted in SP-0231's unfinished migration. |

The other existing pointers were inventoried too: APP-BEHAVIOUR, APP-SETTINGS, WINDOWS-UI,
BROADCAST-RESOURCE, USER-PLAYLIST, INSTALL-TRUST, SITE-REPRESENTATION, REPO-STAMP/REPO-LAYOUT/RULE-DELIVERY,
PACKAGE-VERSIONING, WAVE-PARTICLES and ICON-EXTERNAL retain their earlier scoped claims. The UI pointer
annotations now distinguish historical fixed-size/optional guidance from SP-0231's current migration.
BROADCAST-RESOURCE previously had a product pointer but no adoption row; a partial reader/player row
is added from the existing synthetic parser tests, with physical LAN/backend acceptance unverified.
There is no collection-JSON reader; unsupported collection metadata therefore cannot overwrite local
collections. No counterpart implementation or global registry version/header is changed.

## Exceptions and handoffs

The authoritative exceptions are this product's own catalog registry rows. SP-0231 retains its
2027-03-31 migration and earlier chip deadline. SP-0230 retains its baseline and incomplete walk.
SP-0228 retains joint-run obligations; SP-0237 retains the existing sealed/security, pin-policy,
credential, producer, account-binding and reconnect findings. They are not waived by this audit.

SP-0239, created 2026-10-22 and due 2026-12-31, owns the new scoped catalog/input/documentation and
1.6 broadcast-state and 1.7/1.3 password gaps, remaining legacy gate audit, activation/capture observations and next
authorized Store import evidence. Each registry row states its exact scope, impact, compensating
behavior and review condition. A dated migration plan is not conformance and does not permit a
security, data-safety or Store release blocker to ship.

The permanent records are the frozen activation/profile identity chosen 2026-09-25 and the root
documentation-registry layout retained on 2026-10-22. Review them when their boundary changes or the
contract owner responds; they have no invented expiry. The existing BN address-details variant stays
under its own dated design record and owner review, unchanged.

Recommendations are handed to the catalog owner as dated proposals: clarify Windows stream routing
versus MEDIA-CLASSIFICATION primary types, and permit the existing root-registry profile explicitly.
Proposals change no sealed contract and do not waive a deviation. Existing SP-0228 proposals remain open.

## Verification

Environment: Windows x64, .NET SDK 10.0.402, Release configuration. The app DLL's read-back
FileVersion is `26.1010.0106` (the existing local props stamp, not a new release). The baseline HEAD
is `fab6f62a10a3ab58ece6c9f077736d083feb7be2`; the working tree includes earlier uncommitted work.

| Check | Expected | Actual |
| --- | --- | --- |
| `pwsh -NoProfile -File scripts/check.ps1` | All selected local gates pass | Exit 0, `check: PASS (18 checks)`. Tests: `run=3498 skipped=0`: Core 3155, Exchange seams 326, Drive harness 17. |
| Structured verdict fixtures | Zero, skip, malformed, missing, failed and overflowing results cannot pass | Exit 0, 14 assertions. Full runner reads three fresh TRX files. |
| Store fixtures | 21 accepted, 22/refilled overflow rejected before writes; export BOM retained on no-change | Exit 0, 15 assertions; the production builder is exercised in child processes. |
| Icon catalog check | Current copies/derived files agree with the catalog | `pwsh -NoProfile -File tools/Sync-IconGlyphs.ps1 -Check -CatalogRoot <mounted catalog>` exit 0: 63 meanings, one pending, nine private. |
| Site discovery / release-note fixtures | All generated pages linked; source records and negative cases held | Exit 0: 239 discovery checks, 113 release-note checks; 13 locales x five pages plus 404, zero structure problems. |
| Docs and help | Declared source/render corpus, links and unchanged help baseline | Exit 0: 193 documents, 115 source documents, 231 links; help 12 pages/three locales/21 surfaces/351 links, 40 baseline lines. Both help generation and source gate pass. |
| Local Chrome 151.0.7922.174 and Node VM | Query compatibility, invalid values and scoped rendered interactions | Exit 0, 26 cases. Four captures described in P3's receipt: support EN light/dark 1280, AR dark 360, history DE light 768. No horizontal overflow; footer links/contact/hub, actual pointer theme change and keyboard focus checked. Remote fonts/APIs blocked. |
| Scope and integrity assertions | All targets resolved, no other product/global data-row edits | 29 audit rows. At the first close the README hashes/other rows matched the initial snapshot; the owner then published 1.7/1.3 concurrently. The refreshed closing snapshot records those changes, and each subsequent own write preserves all current non-StreamsPlayer rows. New sources are visible to Git, not hidden by ignore rules. |

The served kit SHA-256 is
`27501a1078653b78a7150250857313625bfe884bcc41a1272b7d7d483cef2895`; the current 3.4 reference is
`4aba1dee10cb902ee21ca4d86e4fbeed3bf7931c64b69fe15619727fd038c141`. Their difference is the recorded
SP-0231 migration, not silently treated as a passing current-kit comparison. Other receipt hashes:

| Artifact | SHA-256 |
| --- | --- |
| App DLL, 2809856 bytes | `346cab0a2e57a52f40583fe767189dec49e496089e3ff0ca4c5bb054923b1c6e` |
| Core DLL, 7936512 bytes | `0eb3e02ea57ffa12c1aee0d11a28db94da316d109a73390ceebf033f107d99e1` |
| Glyph provenance, ICON-SET 0.36 / RENDER 0.24 / EXTERNAL 0.12 | `2557d4430470fb033e917852efbb5e04fe6af48ec70ce8e9ec99e79475a3b83f` |
| Generated Glyphs.xaml | `0aac374cb1b928a8e1b2f0d50d3bea415b671be952560952d84593f98384e7b1` |

WARN: existing nullable/test-analyzer and broadcast-capture platform warnings remain. The optional
demo check is skipped by the existing empty-demo-folder rule, explicitly printed and excluded from
the 18 selected checks. This is not a zero-test pass. The offline snapshot check does not compare
live-bank freshness.

MANUAL / UNVERIFIED: complete WPF keyboard/pointer/theme/high-contrast/scaling/RTL/device/engine
matrix, current-kit contrast/component acceptance, complete help screenshots/F1 walk, joint
phone/server and 4-to-8 broadcast replay on two networks, native capture timestamp read-back,
fresh live-bank/artwork acceptance, live Pages and Store import/re-export. Their owners and deadlines
are above; local gates do not replace them. Playback smoke is not run: this task changes no media
path, native dependency or packaging payload and proposes no release.

Local changes pass; whole-product conformance remains **Partial** with the recorded exceptions and
unverified work. No commit, tag, push, package upload, local deployment or Pages publication occurred.

Post-run focused review: retained search-term cells of an unshipped language column also obey the
21-word cap. The actual builder was exercised again: `tools/store/Test-StoreListing.ps1` exit 0,
15 assertions; overflow preserves prior output. No .NET source changed after the closing run.

Concurrent owner publication was detected while recording final receipts: DEVICE-EXCHANGE 1.7
(section 30, LINK-PASSWORD-2026-10-22) and LIVE-BROADCAST 1.3 (amendment 17, item R), read in full
at 01:24 Europe/Malta. They require producer generated/set/explicit-none password choices and
verifiers on every start/update. This producer has neither profile; an unconditional empty array
would invent consent. Exact exceptions and SP-0239 item 10 carry that gap, coordinated with
SP-0237 item 9; receiver/schema/vector versions do not change. The local tests above are not
claimed as R/DV conformance. The owner's eight changed global/Android rows are preserved; the
initial session snapshot is historical, not a rollback source. Current-write integrity is checked
against registry-before-current-sync.md. No owner contract or counterpart code was edited here.
