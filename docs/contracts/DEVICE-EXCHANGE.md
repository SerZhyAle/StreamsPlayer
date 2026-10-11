# DEVICE-EXCHANGE - pointer

| | |
| --- | --- |
| **Id** | `DEVICE-EXCHANGE` |
| **Version** | 0.17 (draft - `[PROPOSED]` as a whole except the items its own log tags `[CONTRACT]`: L, P, U, W, X; item V and item T are `[BREAKING, draft]`) |
| **Home** | `Contracts/device-exchange/README.md`; the brief for this product is `CONSUMER_PROMPT_streamsplayer.md` beside it |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **receiver** - StreamsPlayer finds a user's broadcasts through the account's exchange server or Google Drive, plays them and accepts casts; and, by the owner's ruling of 2026-10-10 (SP-0217, not built, waits on the contract's role table), **broadcaster** to the one exchange server of its own account. It never publishes a resource and never implements P2P. Broadcasts of other users reach it by their description, through whichever server the description names (SP-0215). |

## What this repository must do to stay conformant

The product reads, and will implement, these parts of the home page, each as one ticket:

| Part of the contract | Ticket |
| --- | --- |
| Sections 5, 6.1, 7.1-7.3, 7.10 - account enrollment, device token, pinning, the control stream, refusal reasons | SP-0200 |
| Sections 6.3, 7.4, 9 - list, subscribe, a broadcast record as a live channel, ended and never deleted | SP-0201 |
| Section 8 - the Drive channel (`drive.appdata` only) | SP-0202 |
| Sections 7.6, 7.7 - the relay endpoint and the endpoint order | SP-0203 |
| Sections 7.5, 7.7 - the tunnel endpoint and its loopback forwarder | SP-0204 |
| Section 7.8 - cast | SP-0205 |

Binding on every one of them: the password is never stored and the device token is kept under the platform's secret store
(Windows DPAPI); a changed certificate is refused and certificate checking is never turned off; a user with no account and no
Google sign-in sees no change (section 9 rule 7); absence from the directory marks a channel ended and never deletes one the
user made (`VERSIONING.md` section 4 rule 5).

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

## Open proposal of 2026-10-10 - not yet in the contract

`Contracts/device-exchange/PROPOSAL-2026-10-10-exchange-connection-string.md` (filed by FMS_W, status **proposed**) widens the
exchange for a client that only views: an `FMSEXCH1:` connection string (also a `.fmsexch` file and a QR payload) that carries a
server endpoint and the pin of its certificate and no secret; a client that needs no producer and no local agent; several
account entries on one device with a `deviceId` per entry; a quick join (string, login, password); a producer that joins one
server and leaves it. No wire change: `schemaVersion` stays 2. The contract is sealed and owned by FastMediaSorter Android, so
**nothing here binds until the owner folds it** as a dated amendment (a MINOR). This product builds to the proposal's text behind
its existing exchange gate - no account, no change (section 9 rule 7) - and treats a different ruling at the fold as a ticket,
not a deviation.

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

What waits on the fold: the vector replay (`connection-string-v1.json` does not exist yet and is never hand-written here), the
exact prose of the messages, the rulings on the questions the proposals list, and - for SP-0217 - the contract's role table,
which still says this product never broadcasts. What does not wait: the codec to the rules of the text, the one-account join and
leave, and the surface.

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

**Conformance evidence.** The contract now carries record vectors (0.16, regenerated in 0.17); this product has no reader to
run them against, so none are run. The handshake reports of rungs 4 and 5 are due when a producer exists.
