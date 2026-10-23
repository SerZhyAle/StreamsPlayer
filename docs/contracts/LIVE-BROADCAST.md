# LIVE-BROADCAST - pointer

| | |
| --- | --- |
| **Id** | `LIVE-BROADCAST` |
| **Version** | 1.3 read 2026-10-22 (SP-0232); prior edition 1.2 (active since 1.0 on 2026-10-15; read in full on 2026-10-21, SP-0228 - the paragraphs below dated 2026-10-07 and 2026-10-15 were written against 0.18 and 0.19) |
| **Home** | `Contracts/live-broadcast/README.md`, with the detail in `11_live_broadcast_contract.md` and `13_contract_amendment_2026-10-07.md`; the exchange wire is `DEVICE-EXCHANGE` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer** of every kind and of the exchange endpoints, and **producer of audio** (microphone and system sound, SP-0217; screen and camera are SP-0223, not built, so the video branches of the writer are unreachable) |

## What this repository must do to stay conformant

**State on 2026-10-21 (SP-0228) - read at 1.2; `Reads` is `partial 1.2`, `Implements` is `partial 1.2`.** The consumer rules 1 to 8, items A to Q, the
amendment of 2026-10-17 and the amendment of 2026-10-19 were read against the tree (111 clauses, evidence in the untracked `temp/SP-0228/`). Fixed in this
round: every descriptor entry point (file, drop, paste, link, a guest's play, a kept directory record) showed "Update StreamsPlayer to import it" for a
higher `schemaVersion`; they now show `DEVICE-EXCHANGE`'s `format-unsupported` row, the old text is deleted from the thirteen dictionaries, and the refusal's
log event names `LIVE-BROADCAST` (amendment of 2026-10-17). Held: rule 8 and item Q (a sealed endpoint is never played as clear bytes, SP-0226), the
per-endpoint order and the silent skips, the first-picture budget with `keyframeIntervalMs` defaulting to 2000, and, for the amendment of 2026-10-19, an
incoming broadcast plays in a window of its own with its own state, bounded at `ExchangeBroadcastResources.MaximumForwarders`. **Recorded, not changed
(dated exceptions in the registry rows):** item M - the producer has no password support, so an outward `RELAY`/`TUNNEL` descriptor carries no `auth` (the
reading FMS_Win filed in `PROPOSAL-2026-10-20-sealed-outward-descriptor-without-auth.md`), and no consumer leg answers Digest, so a protected LAN or
tunnel endpoint cannot play and only the relay can (SP-0215); the FlyleafLib engine holds a broadcast 6 to 10 s behind live (the live-edge corridor, rule
1; LibVLC, the default, does not); a failed video slot has no reconnect in the window itself (amendment of 2026-10-19); the producer starts capture without
a reachable endpoint and a LAN-only broadcast has no description to hand out (producer rules 1 and 3); item N's screen and camera sources (SP-0223).
Owed and not run: every handshake of section 6 against a live phone or second PC, and the reconnect-delay and steady-state numbers of SP-0208.

The consumer rules are in the home page. Repo-local: the descriptor reader is
`src/StreamsPlayer.Core/FastMediaSorterBroadcast.cs` (the `schemaVersion` carrier, the three hand-off
channels, the mode set - the audio kinds of §2.1/§2.2 and, since SP-0159, the camera/video kinds of
§2.3/§2.4 read in the §2.5 shape - and the endpoint choice), and the transport is
`src/StreamsPlayer.App/FastMediaSorterPlaybackTransport.cs`, which holds the one-connection-per-listener
rule for the audio kinds. The video kinds play through the SP-0026 player window
(`src/StreamsPlayer.App/LibVlcVideoBackend.cs` and `FlyleafVideoBackend.cs` behind
`src/StreamsPlayer.App/IVideoBackend.cs`), whose open hands the engine RTP over TCP - the §2.3
recommendation on Wi-Fi - and a live cache, never a catch-up.

SP-0158: the reader (`src/StreamsPlayer.Core/FastMediaSorterBroadcast.cs`) bounds what it follows - at
most eight link/compression unwraps and one 64 KiB inflation budget for the whole read - so a nested
hand-off is refused as an invalid payload rather than followed without end.

**0.18, read 2026-10-07 (SP-0206) - the additions are not implemented, one `[CONTRACT]` gap is open.** The two proposals this
product filed were folded: `sourceId` names a source, not a device (item G, 0.15) and the first-picture budget per mode (item I,
0.17).

- **Held by the reader.** Fields by name; a higher `schemaVersion` refused as its own outcome (`UnsupportedSchema`); unknown
  members and endpoints ignored, which already covers item H's optional `channels` and a `<token>` segment in a LAN URL (a
  top-level `http://..../<token>/live.ts` classifies as video by its extension); `sourceId` is one opaque string, so item G's
  `<deviceSourceId>:<source>` form reads unchanged, and import replaces a stored channel with an equal `sourceId`. Not
  covered by a test: `channels`, a token path.
- **The directory as a fourth hand-off channel is shipped (SP-0201, 2026-10-07)**, and with it item G's rule that of two records
  one `sourceId` keeps, the later `updatedAt` wins - a directory record's descriptor goes through the one reader above, exactly as
  a link's, and `src/StreamsPlayer.Core/ExchangeDirectoryChannels.cs` orders a push's records by `updatedAt`.
- **The endpoint order, the MPEG-TS video shape and the relay are implemented (SP-0203, 2026-10-08; amendment items C, D, H).**
  The leg walks `FastMediaSorterBroadcastAttempts` (Core): every playable endpoint in the producer's listed order, an
  unimplemented transport (`P2P`) and a wrong-mode endpoint skipped silently (§3.2), a reconnect restarting at the top, and the
  legacy top-level address as the single attempt of a descriptor with no usable list. The audio kinds keep their one-connection
  HTTP route, now with the descriptor's leaf pin (`certFingerprint`) in the transport's own TLS callback - never checking off -
  and the LAN connect bound of file 12 §6.2 (1.5 s). The MPEG-TS video shape (`/live.ts`, item C) plays through the SP-0026
  player window with the endpoint's own `targetLatencyMs`. A `RELAY` endpoint (item D) plays as a plain HTTPS GET: audio through
  the pinned HTTP client, video through the engine - through the loopback pin proxy `ExchangeRelayProxy` when the descriptor
  pins the leaf, because neither shipped engine can pin (the engine spike verdict, `temp/sp-0203/spike-verdict.md`; the local
  evidence folder is untracked). The proxy binds 127.0.0.1, exists for one leg, serves one connection and takes a path no other
  program can guess; the buffer is raised only to the endpoint's own `targetLatencyMs`, up to 2000 ms for the exchange
  transports (rule 4). The `TUNNEL` endpoint and `inner` (item J) are read and played through SP-0204's forwarder; that
  ticket is the record for its acceptance. Still owed as a ticket: receivers and cast (item F, SP-0205). The run-and-observe
  with a real phone, and with it item I's first-picture and steady-state numbers on the new transports, stay with the owner.
- **`[CONTRACT]`, item I for the RTSP kinds of 2.3 and 2.4 - first picture measured, steady state owed to the owner.** The
first picture is due within the producer's keyframe interval plus 1 s of connecting, and steady state stays within 2 s. For a
live source the engine's buffer is also the wait before the first picture, so a video or RTSP broadcast opens with the latency
its producer states - `BroadcastLiveCache` (Core): `clamp(targetLatencyMs, 500, 1500)` ms on a LAN transport, up to 2000 on an
exchange one, 1000 when none is stated - and a catalog stream keeps the product's own 15 s (`PlayerWindow.Legs.cs`). The
keyframe proposal this product filed was folded by the contract owner as 0.19 (item L: an endpoint may carry
`keyframeIntervalMs`, absent means 2000; the consumer's live buffer made part of rule 1, which `BroadcastLiveCache` already
is) - observed in the store during SP-0208 on 2026-10-15; the phone producer does not emit the member yet, so the budget
applied is the 2000 default. **Measured against a real phone on 2026-10-15 (SP-0208):** the owner's Galaxy camera
broadcast (`VIDEO_AUDIO`, H.264 720p30) over Wi-Fi LAN opened with the `BroadcastLiveCache` buffer (`cache_ms=500` from a
stated 200) and showed its first picture inside the 3000 ms budget on both samples (bounded above by 2990/2999 ms, with the
producer's keyframe interval measured at 2.000 s in session) - `temp/SP-0208/phone-verdict.md`. For a `MediaKind.Rtsp` row
the SP-0163 displayed-picture gate (`PlayerWindow.xaml.cs:685`, gated on `MediaKind.Video`) does not apply, so `ttff_ms` is
the live edge and `PLAYBACK SHOWN` is the first-picture proof. The margin is thin (worst case: connecting 1000 + interval
2000 + consumer floor 500), and steady state (rule 2) is still the owner's observation - the dated exception in the
registry stays until it is recorded. The measurement path was proven earlier against a loopback stand-in
(`temp/SP-0208/harness-verdict.md`, 2026-10-11), no budget claimed from it.

Because the contract is below 1.0 it may change shape. The registry row carries the date this product last
verified against it; a later reading is not assumed to still hold.

**Conformance evidence.** The contract names no vectors - verification is the handshake in section 6 of
`11_live_broadcast_contract.md`, run against a live broadcast. `FastMediaSorterBroadcastDescriptorTests`
covers the wire shapes this product must accept and refuse, `FastMediaSorterBroadcastAttemptsTests`
covers the endpoint order, the silent skips and the fallback,
`FastMediaSorterBroadcastImportTests` reads each §2.5 descriptor into the channel the player window
plays, `ExchangeCertificatePinTests` and `ExchangeRelayProxyTests` cover the relay's certificate
decision and the loopback pin proxy against a real self-signed TLS fixture, and
`BroadcastLiveCacheTests` covers the per-endpoint buffer.

**Closing owner amendment, 2026-10-22:** item R offers generated/set/none outward-password choices.
The producer offers none of that surface; omission of auth is conformant only after explicit none
choice, and a product with no choice keeps item M. SP-0239 item 10 coordinates password defaults,
secret-store persistence, grants/revocation and DEVICE-EXCHANGE 1.7 verifiers with SP-0237 item 9.
Consumer behavior/schema/vectors do not change; the implementation claim remains partial 1.2.

**SP-0237 corrections, 2026-10-22:** preview capture and video playback share the endpoint decision
and owned pinning/decryption transport; audio applies the same sealed decision. No sealed address
without its broadcast id reaches a plain player. A source with no reachable LAN or sealed exchange
route is refused before native capture, and a LAN-only source gets its description before exchange
announcement. Incoming video failures retain an in-window Retry and expandable contract code;
the signed-out notice uses the contract's cause, advice and details. Flyleaf's broadcast corridor
now uses the endpoint's live-buffer budget; the ordinary-stream corridor is retained. A protected
LAN/tunnel leg this client cannot authenticate is refused with its own message, while the protected
relay keeps its TLS Basic path. Producer password choices, Digest and verifiers remain coordinated
with SP-0239 item 10; live-peer latency and joint acceptance remain outstanding. The implementation
claim is not raised by these scoped corrections.
