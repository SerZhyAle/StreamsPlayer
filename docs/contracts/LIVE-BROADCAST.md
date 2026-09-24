# LIVE-BROADCAST - pointer

| | |
| --- | --- |
| **Id** | `LIVE-BROADCAST` |
| **Version** | 0.9 (draft - sections marked `[PROPOSED]` bind nobody) |
| **Home** | `Contracts/live-broadcast/README.md`, with the detail in `11_live_broadcast_contract.md` |
| **Owner** | FastMediaSorter Android |
| **This product's role** | **consumer** - StreamsPlayer plays a phone or watch broadcast; it never produces one |

## What this repository must do to stay conformant

The consumer rules are in the home page. Repo-local: the descriptor reader is
`src/StreamsPlayer.Core/FastMediaSorterBroadcastDescriptor.cs` (the `schemaVersion` carrier, the three
hand-off channels, the audio-endpoint choice), and the transport is
`src/StreamsPlayer.App/FastMediaSorterPlaybackTransport.cs`, which holds the one-connection-per-listener
rule.

Because the contract is below 1.0 it may change shape. The registry row carries the date this product last
verified against it; a later reading is not assumed to still hold.

**Conformance evidence.** The contract names no vectors - verification is the handshake in section 6 of
`11_live_broadcast_contract.md`, run against a live broadcast. `FastMediaSorterBroadcastDescriptorTests`
covers the wire shapes this product must accept and refuse.
