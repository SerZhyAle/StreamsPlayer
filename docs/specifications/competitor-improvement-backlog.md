# StreamsPlayer - improvement backlog from the competitor review

Research date: 2026-07-19.

## Conclusion

StreamsPlayer already covers the basic scenario better than a "catalog-first" player: explicit catalog refresh, local data with no account, filters, pinning, manual streams, video/RTSP and a grid of live previews. The next gain in value comes not from yet another catalog or a redesign, but from playback reliability, context about what is on air right now, and letting users move their own selection quickly.

Below are proposals for future separate tickets. This is not an approved implementation plan and it does not change the current stream-bank contract.

## Strategic specifications

The recommendations were turned into separate product tickets. They live in the planning archive, which is local to the maintainer and not part of a clone, so they are cited by id rather than linked:

1. `SP-0014` - ICY/Shoutcast now-playing metadata
2. `SP-0015` - Resilient live-stream recovery
3. `SP-0016` - M3U import and export portability
4. `SP-0017` - Local named channel collections
5. `SP-0018` - Stream quality details and filtering
6. `SP-0019` - Local listening history
7. `SP-0020` - Hidden catalog channels and copyable failure reports
8. `SP-0021` - Windows system media controls
9. `SP-0022` - Audio sleep timer

## Priority P0 - visible benefit in the main scenario

### 1. Track title from ICY/Shoutcast for audio streams

Show "station - artist - track" in the bottom panel and, when the data is present, on the card of the current station. Keep only the current line in the session; do not query external services and do not enable telemetry. If the stream sends no metadata, the interface stays as it is today.

Why: the competitor Audials makes the current song and the track history a visible part of the listening experience; the user immediately understands what is on air. [Audials API](https://audials.com/en/company-audials-ag/audials-api)

Constraint: this is already foreseen by the original StreamsPlayer specification (`streams.txt`, B.5) but not yet declared as implemented in the README. It needs correct handling of `Icy-MetaData: 1`, cancellation on stream change, and localized states.

### 2. Automatic live-stream recovery with a clear state

On a transient network error, a stall or a drift past the live edge, retry the connection a bounded number of times with a delay; show "Buffering" and "Reconnecting" as separate states. Once the budget is spent, offer explicit actions: retry, close, remove (manual streams only).

Why: for radio, HLS and RTSP, reliability matters more than new catalog items. VLC explicitly positions network streams as a key input media type. [VLC features](https://www.videolan.org/vlc/features.html)

Constraint: use the live-buffer/retry/watchdog rules already fixed in `streams.txt` rather than inventing unbounded background attempts. Automatic catalog refresh remains forbidden.

### 3. Import and export of user M3U selections

Finish the planned M3U/M3U8 import by URL and add export of only `MANUAL`/`IMPORTED` channels, favourites, or a chosen collection to a portable M3U file. Before an import, show the number of new, matched and rejected rows; the import must stay atomic.

Why: the user can move their selection between players quickly with no account. VLC supports network streams, and M3U remains the practical way to exchange such lists. [VLC for Android - network streams](https://images.videolan.org/vlc/download-android.html)

Constraint: respect the existing URL merge and the priority of `MANUAL`/`IMPORTED`; an HLS manifest must never be mistakenly imported as a channel playlist.

### 4. Several named collections instead of one pinned list

Add local collections, for example "Morning", "News", "Cameras". A channel can belong to several collections; pinning stays a fast shared list and is not replaced by collections. Drag-and-drop and ordering apply only inside a specific collection.

Why: Audials offers several favourites lists for different scenarios, which is more useful than one flat list with a large catalog. [Audials Radio tutorial](https://audials.com/en/one/tutorial/radio)

Constraint: no cloud sync and no account. The model belongs in `StreamsPlayer.Core`, the UI in App.

## Priority P1 - convenience and quality of choice

### 5. Stream quality filter and compact technical details

Use the `format`, `bitrate`, `protocol` and `is_live` values the bank already provides only as optional hints: show format/bitrate in the details and offer a "show streams with the given bitrate" filter. Do not promise actual quality: catalog data can go stale.

Why: Audials offers filtering by stream quality; this is especially useful when choosing a radio station on a limited connection. [Audials Play for Windows](https://audials.com/en/apps/audials-play-windows)

Constraint: do not change the CSV contract, and do not turn missing metadata into an error.

### 6. Listening history and "Recently played"

Keep a bounded local history of successful manual starts: time, channel and the last known ICY line. Offer a separate filter/section and a "clear history" action. Do not record failed probe/preview attempts as listening.

Why: competitors make history available for returning to a station or a track; this lowers the cost of an accidental switch. [Audials Play for Windows](https://audials.com/en/apps/audials-play-windows)

Constraint: data stays on the device; the retention period and the entry limit must be set explicitly in the future specification.

### 7. Hide or report a broken catalog channel

Add "Do not show" (a local blacklist) and "Copy problem report" to the channel menu: URL, name, time and error category - with no automatic sending. An optional future send must be a separate explicit action and a separate decision about the data recipient.

Why: Audials supports excluding unwanted stations, and its infrastructure monitors unavailable sources. [Audials Radio overview](https://audials.com/en/apps/audials-play-windows), [Audials API](https://audials.com/en/company-audials-ag/audials-api)

Constraint: local hiding does not delete the row from the catalog and must survive a refresh by URL.

### 8. System media keys and compact audio control

Support the Windows multimedia keys: play/pause, stop, mute, previous/next within the current view or collection. Show the station name and the current ICY track, when there is one, in the system media session.

Why: this matches the familiar behaviour of desktop audio players and lets the user control the radio outside the application window.

Constraint: an unambiguous "next/previous" model has to be defined first; global shortcuts must not intercept ordinary text input in other applications.

### 9. Sleep timer for audio

Allow stopping the current audio stream after 15/30/45/60 minutes or at a set time. The timer must survive minimizing the window but need not survive an application restart; a manual stop cancels it.

Why: this is a small, clear scenario for radio. It complements the main live player rather than complicating it.

Constraint: do not add the timer to the video player without a separate UX decision; the original specification deliberately leaves the sleep timer out of its controls.

## Not recommended now

- Radio recording, batch recording and automatic song splitting: Audials builds a large paid product around this, but for StreamsPlayer it sharply widens the legal, product and technical surface. [Audials One radio features](https://audials.com/en/one/innovations-2026)
- Podcasts, episode downloads and subscriptions: a content type of its own with a different catalog, storage and lifecycle, not a continuation of live-stream playback.
- Recommendations, an account, cloud sync and analytics: they contradict the current value of a local, private application with no account.
- A station map: Radio Garden makes it the central model of its product, but it does not improve the main StreamsPlayer scenario enough to justify a mapping stack and a new data source. [Radio Garden settings](https://radio.garden/settings)
- Chromecast/casting: the effect is limited for Windows and RTSP; the original specification already marks it as possibly N/A on desktop.

## Recommended decision order

1. Prepare a separate ticket for ICY metadata and resilient recovery (items 1-2): this closes the gap with the declared specification and improves every launch.
2. Then define the contract for M3U import/export and local collections (items 3-4) in `StreamsPlayer.Core`.
3. After that, pick one of the P1 UX improvements: history, system keys or the sleep timer.

## Sources checked

- [StreamsPlayer README](../../README.md) and the original stream specification - the actual current scope and the constraints already accepted. That specification (`streams.txt`, a copy of the handoff brief) was removed from the repository on 2026-09-22: its only home is `DEVELOPER_PROMPT.md` in the shared contract store that `CLAUDE.md` names. Read the references to `streams.txt` above as references to it.
- [Audials Play for Windows](https://audials.com/en/apps/audials-play-windows) - quality filter, track history, radio/TV.
- [Audials Radio tutorial](https://audials.com/en/one/tutorial/radio) - several favourites lists, search and scheduled recording.
- [Audials API](https://audials.com/en/company-audials-ag/audials-api) - the current song and track history as a competitive feature.
- [VideoLAN VLC features](https://www.videolan.org/vlc/features.html) - network stream playback and supported protocols.
- [Radio Garden settings](https://radio.garden/settings) - the favourites model and the map-first product principle.
