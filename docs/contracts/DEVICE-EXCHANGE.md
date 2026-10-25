# DEVICE-EXCHANGE - pointer

| | |
| --- | --- |
| **Id** | `DEVICE-EXCHANGE` |
| **Version** | 1.7 read 2026-10-22 (SP-0232); prior edition 1.5 (active since 1.0 on 2026-10-15; read in full on 2026-10-21, SP-0228 - the sections below dated before that were written against the versions they name) |
| **Home** | `Contracts/device-exchange/README.md`; the brief for this product is `CONSUMER_PROMPT_streamsplayer.md` beside it |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **receiver** and **broadcaster** (`product` `streamsplayer-windows`, items CE and BI; the role table of section 3 has said so since 0.23). It finds a user's broadcasts through the account's exchange server (and the optional Google Drive source, SP-0202; its joint broadcast runs remain open), plays them and accepts casts, and broadcasts the PC's audio to the one exchange server of its own account behind a Settings permission that is off by default (SP-0217; screen and camera are SP-0223, not built). It signs in only (`enroll`, never `register`), publishes no resource, takes no part in the clipboard, is no administration client and implements no server and no P2P. Broadcasts of other users reach it by their description, through whichever server the description names (SP-0215). |

**SP-0232, 2026-10-22:** section 29 DS-DU is audited in the [synchronization audit](../audits/SP-0232-contract-sync.md). The single own-account
model has no separate outgoing selector; announcement/online/guest transitions and the live server/state
line remain SP-0239. Registry implementation/reader claims stay `partial 1.5`; no 1.6 joint proof is claimed.
WEB-BINDING, REMOTE-ADMIN, clipboard and server-only generated corpora remain outside the shipped roles.

**State on 2026-10-21 (SP-0228) - read at 1.5; the row is `partial 1.5`.** Every item since 0.19 and the amendments of 2026-10-17, 2026-10-18 and
2026-10-19 (`RECOMMENDATIONS-2026-10-19.md`) were read against the tree by six independent audits, clause by clause, each with its file, line and
pinning test (evidence in the untracked `temp/SP-0228/`). `REMOTE-ADMIN`, `WEB-BINDING`, the clipboard (section 22) and Drive packets (section 24) bind
nothing built here. `ANYWHERE-ACCESS` has no pointer: its consumers are FastMediaSorter Android, FMS_Win, third-party SFTP clients and CyrFlip, and the
broadcast link with a password is `LIVE-BROADCAST` item M and item BF here. **Defects the reading found and this round fixed** (each with a test that fails
without the fix): item AM - the `enroll` declared 11 `plays` pairs against the bound of 8, which a conforming server answers by closing the stream with no
reply, so no sign-in could succeed; `plays` is now omitted above 8 pairs (3 modes x 4 transports), as items E and CE say. Section 7.4 - a `refused` carries
`reason` only, so a refused `broadcast-start` was taken for the device's refusal and ended the session; a broadcast-scoped reason now answers the start in
flight. Item BN - a connection string whose `fp` names another key than the entry's pin replaced the pin with no confirmation; it now asks with both
fingerprints. Item AD step 3 - the single retry after `id-collision` resent a wiped password. Item AX - the TLS 1.2 floor was set on the account
connection only, and is now on the relay, tunnel and relay-audio connections. Section 7.5 - the producer bridge was cut 10 minutes after it started and is
now cut after 10 idle minutes. Items E, CJ and AO - the cast menu needs `cast` among the server's features, a receiver declaration and a member entry.
Item CK and the amendment of 2026-10-17 - every descriptor entry point shows the `format-unsupported` row; the old "Update StreamsPlayer" text is deleted
from the thirteen dictionaries. **Recorded, not changed (the registry rows carry the dated exceptions):** item D (every leaf is pinned; item AY, built
since SP-0221, removes the cost of a renewal with the same key, so what remains is one acceptance at first contact and a refusal when a CA renews with a new
key), the descriptor endpoint with no `certFingerprint` that the platform rejects (item BK offers a prompt, the product refuses with a message), item BN's
"address first" and "`n` never in place of the host" (the address sits behind a details control, by the owner's decision, item AZ), item BF's `auth` on an
outward descriptor and its Digest legs (the producer has no password support; `LIVE-BROADCAST` proposal of 2026-10-20), item BJ's per-entry binding of an
imported channel, item AC's roles at `enroll` while the Settings permission is off, and the sealed-layer findings of the audit (device-key trust reset by
a vanished device, a crafted descriptor falling back to a plain address, grid previews bypassing the pin, the registering box of item DJ), which are
SP-0237. Five points that only the contract's owner can settle are filed as `PROPOSAL-2026-10-21-streamsplayer-release-46-audit.md`. **Not run:** every joint
rung - phase 1 (FMS_A S4213) and the final run (S4244) need the FMS_Win test server, a phone and the two networks; the rows stay `partial` for that reason.

## What this repository must do to stay conformant

**SP-0202, 2026-10-25:** the Drive premise is proven by the Android-to-desktop run of FMS_W 203
(2026-10-23) and this product's own client listing the Android probe (2026-10-25). The client registration
and project hashes agree with FMS_W 203 section 5.5. The Google source now reads device and broadcast
wrappers only, caches unchanged records, judges TTL on Drive's clock, and lists them alongside the exchange
directory. It polls only in the visible, restored list window. Own audio broadcasts also announce through
Drive, renew within TTL/3 and remove their records on end. The one scope is `drive.appdata`; refresh tokens
use a separate Windows-user DPAPI file and sign-out requests revocation. Core owns parsing, bounded REST,
OAuth and the writer; App owns browser interaction, protection and window lifetimes. Server media still
requires sealing; item DO permits the announcement's outward descriptor in Drive. Local sign-in, list,
minimize, restore, close and sign-out were observed with request counts. A real phone's broadcast playing
and ending, and the phone listing this PC with the exchange server off, remain joint-run criteria. This
does not advance the overall contract version or certify those joint runs.

The product reads, and will implement, these parts of the home page, each as one ticket:

| Part of the contract | Ticket |
| --- | --- |
| Sections 5, 6.1, 7.1-7.3, 7.10 - account enrollment, device token, pinning, the control stream, refusal reasons | SP-0200 |
| Sections 6.3, 7.4, 9 - list, subscribe, a broadcast record as a live channel, ended and never deleted | SP-0201 |
| Section 8 - the Drive channel (`drive.appdata` only) | SP-0202 |
| Sections 7.6, 7.7 - the relay endpoint and the endpoint order | SP-0203 |
| Sections 7.5, 7.7 - the tunnel endpoint and its loopback forwarder | SP-0204 |
| Section 7.8 - cast | SP-0205 |
| Section 5.5 and section 16 items Y to AF - the `FMSEXCH1` connection string, the quick join, one server joined and left | SP-0213, SP-0214, SP-0216 |
| Section 16 items AC and AF - the one outgoing entry and leaving; other users' broadcasts by description | SP-0214, SP-0217, SP-0215 |
| Section 17 items AG to AL - the resource PIN boundary, as a directory reader | SP-0220 |
| Section 27 items DE to DR - sealed content: the device key, the box, the sealed relay and tunnel, the four states, the honest text | SP-0226 |
| Section 5.1 items AO, AR, AZ, AT, AV, AW, AY and section 5.5 item BN - sign-in only, the two kinds of account, password change, signed out by the account, sign out on this device, the key pin of a renewal, QR pictures | SP-0221 |

Binding on every one of them: the password is never stored and the device token is kept under the platform's secret store
(Windows DPAPI); a changed certificate is refused and certificate checking is never turned off; a user with no account and no
Google sign-in sees no change (section 9 rule 7); absence from the directory marks a channel ended and never deletes one the
user made (`VERSIONING.md` section 4 rule 5).

**State on 2026-10-21 (SP-0225):** the ladder of item CY, the table of item CZ and the display rules of item DA are built as the
connection check, with the `format-unsupported` row of the amendment of 2026-10-17. The fifteen steps and the 24 rows this product
raises are Core (`ExchangeDiagnosisStep`, `ExchangeFailureRows`, `ExchangeDiagnosisEngine`); the words are the table's in EN, RU and
UK, held in CI by `ExchangeFailureTextTests` against a vector that `tools/localization/check-exchange-terms.ps1` exports from the
catalog, and translated from EN in the other ten languages. A refusal is the server's answer, so the link steps before the one it
names are shown as passed and nothing is probed; a failure with no reason on the wire (`unreachable`, `unanswered`, `tls-failed`) is
walked, probing what this PC can see - a bounded connection to the address the person already uses, the adapters, a name lookup -
and offering what it cannot (the router's guest network, the server computer's Windows profile and clock). A stream closed with no
frame after this product's own envelope is "the server did not answer" (item AN). The check runs only from a button (Settings, the
list window, a failed broadcast's window), shows what happened and then what to try (item DA), keeps the address and port behind its
details control, and leaves one line of codes in the session log. `version-unsupported` and `format-unsupported` replace every
"update the program" text (item CK). Not raised here, because this product has no such surface: the resource rows, `login-taken`,
`invitation-invalid` (it never registers), `server-offline`, the clipboard rows and the sealed rows of SP-0226. The registry row and
the version this product records stay SP-0228's.

**State on 2026-10-21 (SP-0226):** section 27 (items DE to DR) is built, read at 1.5; the registry row and the version this
product records stay SP-0228's. Every broadcast this PC sends through a server is sealed (items DK, DL, DM): a P-256 device key per
account entry, made before `enroll` and kept in the DPAPI-protected account record (item DF); the broadcast's keys in a box
addressed to the entry's other devices this program lists, with the capability and a first-seen key (items DG, DH); the relay
upload as records under an epoch key with a replacement upload when a device leaves (items DL and N); the tunnel answered with TLS
inside, on a leaf made for the broadcast and pinned by `tunnelPin` (item DM). Every broadcast received is opened through the
loopback proxy or forwarder the product already puts in front of an exchange endpoint, from the record's box, a guest's grant box
(first-seen sender key per grant, item DJ) or the keys of an imported link (item Q, kept in the secret store apart from the
channel). An exchange endpoint that is not sealed is never played as clear bytes (item DP) and the four states of item DR are said
in the person's language; the honest text of item DE is on the Settings page and in the three READMEs. The suite replays
`vectors/e2e-v1.json` and the Auth-mode vector of RFC 9180 A.3.3, and reads the sealed frames of `vectors/fmsw-234/` (the FMS_Win
server's; complementary evidence, no joint replay is claimed). HPKE is written from the RFC on the base class library (no package).
**Not built here:** the sealed clipboard and sealed folder `access` (this product takes no part in either), granting a broadcast to
a guest from this PC (no grant surface), the Drive path (item DO), and the video producer (SP-0223 uses the same layer). **Open:**
the joint runs with a real FMS_Win server and the phone, on both networks (`BlockNeedUserTest`), and two points for the contract's
owner: item DL does not say how a listener learns the chunk flag that is part of a media record's associated data (this product
tries the flags a producer sets), and the corpus's stand-in broadcast payload spells the pair `username` where item BF says `user`
(both are read).
**State on 2026-10-21 (SP-0222):** the account's broadcast list of item CI is built; the registry row and the version this
product records stay SP-0228's. The directory is fetched because a person looked: the session sends no `list` and no
`subscribe` when it comes up, the open list takes a lease that sends `list`, then `subscribe` once per session, and follows
`changed`, and a one-shot ask (the cast menu's receivers) sends `list` alone. The wire has **no `unsubscribe`** and the
control stream also carries casts and this PC's own broadcasting, so closing the list ends the following on this side only -
later pushes are read and discarded, no `list` answers a gap, a reopen sends `list` and never a second `subscribe`; an
`unsubscribe` would make "ends the subscription" literal and is a question for the contract's owner, not decided here. A
`guest` entry is read by a short read-only session of its own while the list is open (`hello`, `list`, `subscribe`,
keepalives, `bye`; nothing a guest may not send) and is listed under its own heading. A broadcast record's `credentials`
(items BF and BD) are read into memory only - never printed, logged, stored or copied into the library - and play the
person's own protected broadcast with no question; a `sealed` box is opened as SP-0226 states. Incoming broadcasts play in windows of
their own, bounded at `ExchangeBroadcastResources.MaximumForwarders`; Drive rows wait for SP-0202.

**State on 2026-10-15 (SP-0221):** items AO to AZ, AT, AV, AW, AY, BN and BR of 1.1 are built; the registry row and the
version this product records stay SP-0228's. This product **signs in only** (`enroll`; item AZ) and never sends `register`:
a string carrying `i` is read as a sign-in, its code is validated and dropped (item AR) - never sent, stored or logged.
`enrolled` and `welcome` carry `accountKind` (item AO): an account of kind `member` is the one own entry that may
broadcast (item AC), and a `guest` account is held beside it as a stored entry that is never connected from the sign-in and
announces nothing (a guest never takes the own slot and never makes this PC leave it; an unknown kind reads as guest).
`password-change` runs on the live control stream with the old and the new password as character arrays, one request at a
time (item AW), and the answer's fresh token is stored before anything else; a `refused` that arrives while one is out is
its answer and never this device's revocation. `signed-out` (item AT) keeps the entry, its device id and its server and
asks for the password again, and nothing retries. Sign out on this device is item AB step for step (item AV). The pin of
an entry holds the leaf fingerprint and the key (item AY): a renewal with the same key is accepted with no prompt and the
leaf is replaced; another key is a changed certificate. TLS 1.2 is the floor (item AX). A QR is read through ZXing.Net
from a picture file or the clipboard and goes through the same codec as pasted text (item BN: reading never connects).
The address and port sit under a details control by the owner's decision; item BN's "shown first" reads the other way, and
SP-0228 carries that as a dated exception candidate. The camera door is SP-0229.

**State on 2026-10-13 (SP-0220):** read at 0.19. The 0.18 fold is adopted: the `FMSEXCH1` codec, the `.fmsexch` file
reader and the byte-exact vector replay (SP-0213, Verified); the quick join behind the trust rule - password used only
for the one `enroll` exchange and then cleared, token under DPAPI (SP-0216, Implemented) - on one account entry whose
join of another server leaves the first (its broadcasts end, then `bye`, then `revoke`), whose entry identity is the
address with the case-insensitive login, and whose fresh `deviceId` regenerates once on an `id-collision` answer
(SP-0214 with SP-0216; 16 items AA, AB, AC, AD). Other users' broadcasts play by description alone, no entry, trust by
the endpoint's `certFingerprint` else the platform chain (SP-0215; 16 item AF - local verification complete, a real
foreign server is `BlockExternal`). A client-written connection string (16 item AE) is not exercised: this product
implements no writer surface, and 5.5 makes a string a document, never a message. The 0.19 fold binds this product as
a directory reader: `enroll` declares no `capabilities`, so a conforming server hands it no `access` for a protected
resource (17 item AG); the reader imports no resource record at all, and `ExchangeResourcePinBoundaryTests` plus the
`protection-v1.json` replay hold that boundary (17 items AG and AJ, 9 rule 10). SP-0202 (Drive) stays Draft behind the
premise experiment of 8.2: the rung-3 instrument for it exists since 2026-10-14 (`tools/StreamsPlayer.DrivePremiseHarness`,
a diagnostic tool and no product code - it signs in with PKCE and a loopback redirect, `drive.appdata` only, and writes
its probe in the 8.1 wrapper of the corpus), while the run with the project's two OAuth clients stays with the owner.

**State on 2026-10-07:** read at 0.17. SP-0200 implements account enrollment, DPAPI user-scoped storage, TLS leaf verification,
receiver capability pairs and the control stream. SP-0201 implements the directory on that session - sections 6.1, 6.3, 7.4
(`list`, `subscribe`, `directory`, `changed`, the revision-gap re-list) and the boundary rules 1, 3, 5, 6 of section 9: the record
reader lives in `src/StreamsPlayer.Core/ExchangeDirectory.cs` and the boundary rules (ended, never deleted; a push refreshes by
`sourceId` and adds nothing) in `src/StreamsPlayer.Core/ExchangeDirectoryChannels.cs`. **SP-0203 (2026-10-08) implements 7.6 and
7.7:** the relay listen plays as one HTTPS GET - audio ADTS through the transport's own pinned TLS callback, video MPEG-TS
through the engine, through the loopback pin proxy `ExchangeRelayProxy` (Core) when the descriptor carries `certFingerprint`,
because the engine spike of SP-0203's phase 0 confirmed neither shipped engine pins a leaf; a mismatched pin is refused and
checking is never turned off (item V). The endpoint order of 7.7 and the section 9 rules 4 and 6 hold through
`FastMediaSorterBroadcastAttempts`: the producer's listed order, silent skips of unimplemented transports, restart at the top on
a reconnect, one connection per listener (the recovery probe pair never touches a broadcast row), and the receiver capability
list `ExchangeCapabilities.Plays` extended to exactly what plays. Drive and cast work remains separate; the existing LAN, link,
file and barcode paths of `LIVE-BROADCAST` are preserved. Local protocol, TLS fixture and proxy checks
(`tests/StreamsPlayer.Exchange.Tests/`, `tests/StreamsPlayer.Core.Tests/ExchangeRelayProxyTests.cs`) do not replace the
handshake against the FMS_W server when it exists; rung 4 stays `BlockExternal` until that server and one producer exist, and
SP-0203's LAN run-and-observe with a real phone stays with the owner.

**Owner choice for SP-0200:** pin every server leaf, including platform-validated leaves, and require explicit approval for
replacement. This is deliberately stricter than item D of 0.17 and requires user action after a CA leaf renewal.

## The 2026-10-10 connection-string proposals - folded as 0.18 (section 16)

`Contracts/device-exchange/PROPOSAL-2026-10-10-exchange-connection-string.md` (FMS_W) and
`PROPOSAL-2026-10-10-connection-string-entries-and-leave.md` (FastMediaSorter Android) were folded by the
contract owner as 0.18 on 2026-10-11 (section 16, items Y to AF, ticket S4153), with every question of both
answered. This product builds the folded text: the table below is the ask-to-ticket map. The three
proposals this product filed the same day stay **proposed** and unfolded (contract section 12); where one of
them repeats a byte rule `vectors/connection-string-v1.json` already fixes, section 5.5 governs.

**The owner's ruling of 2026-10-10 on this product's shape.** The device has **one** exchange server, the one it broadcasts to
and lists its directory from; it may join it by the quick scheme and leave it. A connection to a broadcast of another user,
possibly through that user's exchange server, is part of the broadcast's description and is unlimited in number; it needs no
account entry. So Ask 3 (several entries) is not exercised by this product.

| Proposal ask | What this product does | Ticket |
| --- | --- | --- |
| Ask 1 - the string and the file; the trust rule for `fp` | Read it in Core, refuse what is not ours with one message; the QR form is the same text and is not read on this PC | SP-0213 |
| Ask 4 - one server, join and leave cleanly | One account; joining another leaves the first; the leave ends a running broadcast first, then `bye`, `revoke` of its own `deviceId`, forget | SP-0214 |
| Ask 3 - several entries | **Not exercised**: one account; other users' broadcasts come by description (see the ruling above) | - |
| Other users' servers (no ask; `LIVE-BROADCAST` item D, `DEVICE-EXCHANGE` 7.5-7.7) | Any number, no account, trust by the endpoint's fingerprint; a self-signed server without one is refused with a message | SP-0215 |
| Ask 2 and Ask 5 - a client with no local agent; the quick join | Import a string or a file in Settings (the main window also recognises one), see the server, type the password, see the broadcasts | SP-0216 |
| Role `broadcaster` (`DEVICE-EXCHANGE` 3, 6.1, 7.4-7.8; `LIVE-BROADCAST` items A, C, D, G-K) | Microphone, system sound, a monitor and a camera of this PC through `RELAY`, LAN `HTTP` and `TUNNEL`; cast to a named receiver; consent in two steps | SP-0217 |
| Ask 6 - the operator's connection-string surface | **Not applicable**: this product implements no server | - |

What the fold answered: the vector replay exists (`vectors/connection-string-v1.json`, replayed byte for byte - see the
conformance paragraph below), the prose of the messages is the folded 5.5 and the fold's items, the owner's answers are
section 16, and - for SP-0217 - the role table of section 3 still says this product never broadcasts, so the broadcaster
stays behind the Settings permission and does not ship.

Findings filed beside the contract (`PROPOSAL-2026-10-10-streamsplayer-connection-string-findings.md`, status **proposed**): the
string steers a password and must be shown before it is trusted (F1); Ask 2 names `resource-consumer`, and this product is a
receiver and a broadcaster (F2); an entry's identity and the dead device a removal leaves (F3, F4); ended-by-absence needs the
entry (F5); the file reader's BOM, the units of the limits and the host's ASCII form (F6 to F8); this product's pin-every-leaf
policy against item D (F9); Ask 4 for a broadcaster (F10); listening to another user's broadcast needs no entry (F11).

**Recorded exception.** The owner's ruling of 2026-10-07 (SP-0200) pins every server leaf, a platform-validated one included,
after the user accepts its fingerprint. That is stricter than item D above and is a dated exception in the registry since
2026-10-10, closed by a ruling on finding F9.

What the proposals this product filed became (all in `Contracts/device-exchange/` unless noted):

- **directory frame limit** - the record-too-big refusal `capacity` was folded (14.1 item A); the paging and record-ceiling asks
  were **not** taken: item T keeps 16384 until `welcome`/`enrolled` and 262144 after, no paging. Anything in SP-0200/0201
  written around paging is moot.
- **Drive channel** - folded as 14.2 items B and C: **the Drive experiment (rung 3) comes before any consumer work**, expiry is on
  Drive's clock (`modifiedTime` against the response `Date`). SP-0202's gate (a) already says so.
- **certificate trust** - folded as 14.3 item D: a certificate the platform validates is never pinned, a failing leaf is pinned
  only after the user accepted its fingerprint, a pin is replaced only by the user. The public-key (SPKI) pin form is an open
  owner question; item V makes `connect` TLS-only and is `[BREAKING, draft]`.
- **receiver capability pairs** - folded as 14.4 item E: `receiver.plays`, `{mode, transport}` pairs.
- **`sourceId` and the first-picture budget** (in `Contracts/live-broadcast/`) - folded, see `LIVE-BROADCAST`.

Other items that bind the six tickets: `keepaliveSeconds` (item F, SP-0200), cast reasons `declined`, `timeout` and
`unsupported` and no `access` on `cast-offer` (item R, SP-0205 - it does not yet name `timeout`), `id-collision` and the scope of
ids (items G, H), a resource without `access` is listed, never skipped (item X).

**Conformance evidence.** The product's suite replays the contract's artifacts it can, each a byte-exact vendored copy with
its provenance beside it: `vectors/connection-string-v1.json` (104 cases, `ExchangeConnectionStringVectorTests`), the
`directory` frames of `vectors/protection-v1.json` (`ExchangeProtectionVectorTests`), the sealed frames of `vectors/fmsw-234/`
and `vectors/e2e-v1.json` (SP-0226), and, since SP-0228 (2026-10-21), the three corpora the real server generated:
`vectors/server-frames-v2.json` (153 frames, 26 streams, `ExchangeServerFramesReplayTests`: every server frame is decoded
and every refusal is mapped to its row; the frames this product sends - `hello`, `enroll`, `revoke`, `attach`, the tunnel
`connect`, `cast-answer`, `password-change` and the upload chunk steps - are compared byte for byte),
`vectors/server-records-v1.json` (`ExchangeServerRecordsReplayTests`: the 5 accepted devices, the 6 accepted resources
ignored and the 5 accepted broadcasts skipped without an error, the stale `resource-16KiB-exact` read by item BS) and
`vectors/fmsw-224/frames-v2.json` (`ExchangeAccountLifecycleReplayTests`: 36 streams, the account kinds, the 7 registration
refusals, `password-changed`). The session tests of `ExchangeLoopRecoveryTests.ServerCorpus.cs` feed the same server frames
to the real `ExchangeSourceService` over TLS loopback. Not replayed, and why: the cases that only a server can answer
(`closes-the-stream`, bucket exhaustion, the cast timeout, the HTTP dialogs); `broadcast-start`, `-update`, `-end`,
`upload`, `cast` and `cast-stop`, built inline in `BroadcastProducerService`, which no test project compiles; `register`
and `account-delete`, which this product never sends; `vectors/records-v1.json` and `vectors/drive-packets-v1.json`, the
Drive corpora (sections 6, 8.1 and 24) - this product reads no Drive file, SP-0202 is gated by the premise experiment of 8.2
and item CU gives it the announcement only; `fmsw-229` (clipboard) and `fmsw-245` (administration), roles it does not
declare. The vendored folder `tests/StreamsPlayer.Core.Tests/Vectors/server/` records the file-name difference between the
generator's `PROVENANCE.txt` and the catalog. The negative-access run of 17 item AL is the producer's joint client/server
evidence, not this product's to produce; the handshake reports of rungs 4 and 5 are due when a producer exists.

## Original resource PIN boundary (0.19)

DEVICE-EXCHANGE section 17 AG-AL keeps the original folder PIN separate from account membership,
TLS certificate pins and the shared SFTP password. StreamsPlayer consumes broadcast records,
not SFTP resource records, and does not advertise `pin-v1`. A protected resource cannot become
an imported channel, preview, restored playlist item or playback attempt through its directory
`access`. `ExchangeResourcePinBoundaryTests` covers known, future and malformed lock metadata,
and the `directory` frames of `vectors/protection-v1.json` replay through the same reader
(`ExchangeProtectionVectorTests`): the record with and without `access`, at each transcript
revision, imports as nothing and is never an error (9 rule 10). Folder attachment and the resource
PIN proof remain unsupported in this product. The day a version of it attaches resources, it
implements the `pin-v1` capability or keeps the explicit deny - one of the two, never neither
(17 item AJ).

**Closing owner amendment, 2026-10-22:** section 30/item DV and LIVE-BROADCAST item R were
published concurrently with this run. At that date no password-choice profile or verifiers member
existed in the producer; an unconditional empty verifiers array would have invented the person's
choice of none and was not a safe patch. Partial 1.5 is retained until the joint observations of
the next paragraph are made.

**SP-0223 producer, 2026-10-25:** every `broadcast-start` and `broadcast-update` now carries an
envelope-level `verifiers` (the member the FMS_Win server reads): one entry per pair - the account's
own pair and the link's - with a 16-byte salt and PBKDF2-HMAC-SHA256 at 600000 iterations, and an empty
array exactly when the person chose no password. The
sealed box carries the own pair as `credentials` whatever the link's choice, on every re-seal. A
change on the air is a `broadcast-update` with the new list. Captured frames recompute to the
password the person is told (`ExchangeLoopRecoveryTests`). Credentials do not go to Drive: a Drive
announcement carries `auth` and no pair, so a device that learns of a broadcast only through Drive
asks for the password. Not done: a relay that checks the list, a listener that answers it, and the
joint run of LIVE-BROADCAST section 6.

**SP-0237 corrections, 2026-10-22:** directory absence now retains a device's trusted key; off-curve
keys are excluded without aborting other recipients; a registering grant is hidden and establishes
no sender trust. Re-establishing a broadcast refreshes recipients and rotates the departed device's
group key and epoch; a cast refreshes the directory and writes its box before offering only to an
addressed recipient. Sealed addresses without the required broadcast path are refused before any
plain transport. Clear credentials are ignored whenever a sealed member exists. Imported directory
channels carry their listing entry's stable identity and remain with a not-listed label on removal.
Enrollment validates the credential bounds before connecting and declares broadcaster independently
of the capture permission (owner answer in this session); existing entries need explicit re-enrollment
to change their server-held roles. An unpinned descriptor leaf the platform rejects can be accepted
explicitly for that channel, with trust stored separately from account trust (owner answer in this
session). These local corrections do not certify the outstanding producer-password or joint-peer work.
