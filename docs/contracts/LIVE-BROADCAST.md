# LIVE-BROADCAST - pointer

| | |
| --- | --- |
| **Id** | `LIVE-BROADCAST` |
| **Version** | 0.13 (draft - the camera and video kinds of §2.3/§2.4 are `[CONTRACT]`; the relay/P2P transport of `12_relay_p2p_exchange_protocol.md` stays `[PROPOSED]`) |
| **Home** | `Contracts/live-broadcast/README.md`, with the detail in `11_live_broadcast_contract.md` |
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

Because the contract is below 1.0 it may change shape. The registry row carries the date this product last
verified against it; a later reading is not assumed to still hold.

**Conformance evidence.** The contract names no vectors - verification is the handshake in section 6 of
`11_live_broadcast_contract.md`, run against a live broadcast. `FastMediaSorterBroadcastDescriptorTests`
covers the wire shapes this product must accept and refuse, and
`FastMediaSorterBroadcastImportTests` reads each §2.5 descriptor into the channel the player window
plays.
