---
name: project_contracts
description: Contract relationships, STREAM-BANK protections, APP-BEHAVIOUR UX invariants, and DIAGNOSTIC-REPORT constraints
type: project
---

# Contract Conformance & Shared Interfaces

- **Contract repository rules:**
  - Contracts are cited by ID, never copied into the tree or compiled as external inputs (`docs/contracts/*.md`).
  - `CLAUDE.md` first line is the sole place that names the contract store location.
- **STREAM-BANK consumption & UserAuthoredChannels (SP-0089):**
  - Catalog refreshes merge by URL and protect user-created rows.
  - `UserAuthoredChannels.Identify` prevents deletion of channels that have user pins, collection memberships, or history entries even if dropped from upstream bank.
  - New user-attached channel properties must be added to `UserAuthoredChannels.Identify`.
- **APP-BEHAVIOUR rules (owned here):**
  - Destructive confirmations default button focus to Cancel/No.
  - `SettingsWindow` holds values only; operations belong in `ToolsWindow` (`Operations > Tools`) or context menus.
  - User-facing failures format cause from exception type via `FailureCauseText`, never displaying raw `exception.Message`.
- **DIAGNOSTIC-REPORT invariants (owned here):**
  - Diagnostic archives must be explicitly triggered by the user.
  - Environment summary is strictly structural metadata and counts (never channel URLs, titles, or history).
  - All credentials, tokens, and local profile paths are sanitized with `[REDACTED]`, `<APP_DATA>`, and `<USER>`.
- **DEVICE-EXCHANGE directory boundary (SP-0201, 2026-10-07):** a broadcast record's descriptor goes through the one
  `FastMediaSorterBroadcastDescriptor` reader - no gentler path for records that arrive over a socket. A channel the directory
  created (`FastMediaSorterBroadcastInfo.DirectoryBroadcastId`) is marked ended (`DirectoryEndedAt`) when its record leaves -
  kept, never deleted; a manual, file or link row never gains the directory origin and is never marked ended by a record
  leaving; a background push refreshes stored channels by `sourceId` (later `updatedAt` wins, the row's title survives) and
  adds nothing (owner's Option A). A relay (`https://../v2/b/<id>/stream`) or tunnel (`fmsx:`) address is the listening
  capability: `BroadcastCapabilityAddress.CarriesCapability` gates share/export with a warning that says so.
- **USER-PLAYLIST (owned here):**
  - Supports Extended M3U8 (`.m3u8` / `.m3u`). Rejects `#EXT-X-` HLS media manifests.
  - Non-destructive import deduplicating by normalized URL.
