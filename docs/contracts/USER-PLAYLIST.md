# USER-PLAYLIST - pointer

| | |
| --- | --- |
| **Id** | `USER-PLAYLIST` |
| **Version** | 0.12 (draft) |
| **Home** | `Contracts/user-playlist/README.md` |
| **Owner** | **this product**. StreamsPlayer defines the specification and conformance vectors |
| **This product's role** | **producer and consumer** |

## What this repository must do to stay conformant

1. **Extended M3U8 carrier:** Import and export `.m3u8` / `.m3u` (Extended M3U8). Amended 2026-09-26 (contract section 6 item A, SP-0177): this is the one required carrier. The JSON carrier (`.sza-playlist.json`) is optional until a consumer needs it, and this product has no JSON reader or writer; such a file imports nothing.
2. **Non-Destructive Import:** Importing streams must never overwrite or delete local user-created (`Manual`) or previously `Imported` channels.
3. **De-duplication:** Deduplicate entries by exact ordinal match on normalized stream `url`. Skip duplicate URLs within the same import file.
4. **HLS Rejection:** Reject `#EXT-X-` media manifests during playlist import with `Status: HlsManifest` (0 entries imported).
5. **Title Sanitization & Fallback:** Flatten newlines in channel titles upon export (`#EXTINF:-1,<title>`), and fall back to `Uri.Host` when importing untitled entries.

Evidence: `src/StreamsPlayer.Core/M3uPlaylistParser.cs`, `M3uPlaylistWriter.cs`, `M3uPlaylistFile.cs`, `M3uImportService.cs`, `MainWindow.ImportExport.cs`. Tests: `tests/StreamsPlayer.Core.Tests/M3uPlaylistParserTests.cs`, `M3uPlaylistWriterTests.cs`, `M3uPlaylistFileTests.cs`.
