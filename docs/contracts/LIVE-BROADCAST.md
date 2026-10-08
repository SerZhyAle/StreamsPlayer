# LIVE-BROADCAST - pointer

| | |
| --- | --- |
| **Id** | `LIVE-BROADCAST` |
| **Version** | 0.18 (draft - the audio kinds and the camera and video kinds of 2.1-2.4 are `[CONTRACT]`; most of what 0.15-0.18 adds is `[PROPOSED]`: `13_contract_amendment_2026-10-07.md` items G, H and J; item I is `[CONTRACT]` for the RTSP kinds; item K binds when the relay flips) |
| **Home** | `Contracts/live-broadcast/README.md`, with the detail in `11_live_broadcast_contract.md` and `13_contract_amendment_2026-10-07.md`; the exchange wire is `DEVICE-EXCHANGE` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer** - StreamsPlayer plays a phone or watch broadcast; it never produces one |

## What this repository must do to stay conformant

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
- **`[CONTRACT]`, unmeasured - item I for the RTSP kinds of 2.3 and 2.4.** The first picture is due within the producer's keyframe
  interval plus 1 s of connecting, and steady state stays within 2 s. For a live source the engine's buffer is also the wait
  before the first picture, so a video or RTSP broadcast opens with the latency its producer states - `BroadcastLiveCache`
  (Core): `clamp(targetLatencyMs, 500, 1500)` ms on a LAN transport, up to 2000 on an exchange one, 1000 when none is stated -
  and a catalog stream keeps the product's own 15 s (`PlayerWindow.Legs.cs`). The leg logs time to first byte and time to first
  picture as separate values and claims neither (open question 1 of SP-0203). Neither number has been **measured** against a
  real phone (SP-0208, and the dated exception in the registry). The contract states no keyframe interval for the RTSP kinds, so
  the budget is not a number a consumer can check; a proposal to add an optional `keyframeIntervalMs` is filed beside the contract
  (`PROPOSAL-2026-10-07-keyframe-interval-and-consumer-buffer`).

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
