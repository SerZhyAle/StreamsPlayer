# LAN-DISCOVERY - pointer

| | |
| --- | --- |
| **Id** | `LAN-DISCOVERY` |
| **Version** | 0.9 (draft) |
| **Home** | `Contracts/lan-discovery/README.md` |
| **Owner** | shared (candidate owner FMS Companion) |
| **This product's role** | **consumer** |

## What this repository must do to stay conformant

1. **Service Types:** Discover local broadcast services under `_fms-bcast._tcp.local` and `_fms-sftp._tcp.local`.
2. **Graceful Fallback:** If mDNS / UDP multicast is blocked by local firewalls or network isolation, discovery fails silently without crashing, preserving manual stream URL input.

Evidence: `src/StreamsPlayer.App/MainWindow.FastMediaSorterBroadcast.cs`, `FastMediaSorterPlaybackTransport.cs`.
