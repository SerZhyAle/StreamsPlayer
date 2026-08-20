<p align="center">
  <img src="docs/assets/streamsplayer-icon-256.png" alt="StreamsPlayer icon" width="112">
</p>

<h1 align="center">STREAMS Player</h1>

<p align="center">Internet radio, live video, and RTSP for Windows.</p>

<p align="center">
  <a href="https://serzhyale.github.io/StreamsPlayer/">Website</a> ·
  <a href="https://github.com/SerZhyAle/StreamsPlayer">Source</a> ·
  <a href="https://serzhyale.github.io/StreamsPlayer/privacy.html">Privacy</a>
</p>

<p align="center">
  <strong>Language:</strong>
  <a href="README.md">English</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.uk.md">Українська</a>
</p>

> **Release status:** STREAMS Player is released and free. Get it on the
> [Microsoft Store](https://apps.microsoft.com/detail/9NBTD5SXB8TB), as a
> portable ZIP from the [latest GitHub Release](https://github.com/SerZhyAle/StreamsPlayer/releases/latest),
> or with `winget install SerZhyAle.StreamsPlayer`. No account, no ads, no
> telemetry; the source is MIT.

## A calm player for the stream in front of you

| Find a channel | Keep your choices | Play the right media |
| --- | --- | --- |
| Browse a curated catalog and filter by category, topic, language, country, or media type. | Search, sort, pin, and add your own streams without an account. | Listen to radio in the main window or open live video and RTSP in a focused player window. |

STREAMS Player is an independent Windows desktop application for internet radio,
live video, and RTSP channels. It consumes the published FastMediaSorter stream
bank as an external data contract; it does not share FastMediaSorter application
code or features.

## What it does

- Refresh the catalog only when you choose to. There are no surprise background
  catalog downloads.
- Watch the catalog and the preview pictures arrive: both downloads show how much
  is left, and either can be stopped at any point. Stopping one leaves your
  channel list exactly as it was.
- Fill the channel list without any internet from the copy of the catalog that
  ships inside the app. It is offered once on a first launch with an empty list,
  offered again whenever an update cannot go through, and available at any time
  from the **Playlists (M3U)** tab in Settings. It only adds and updates channels
  - it never removes any - and the app always says the list came from the
  built-in copy and how old that copy is, so it is never mistaken for a fresh
  download. Applying it is always your choice; nothing happens on its own.
- Read the published stream bank with RFC-4180 CSV, ZIP entry-order, and optional
  favicon-atlas checks.
- Protect your `MANUAL` and `IMPORTED` rows while updating the catalog by URL.
- Keep a catalog channel you made something of, even after the catalog stops
  listing it. A row you never touched is still removed, but one carrying a pin,
  a collection, a playback mark, listening history, or an icon you chose is
  retired instead of deleted: it keeps its identity, so pins, collections, and
  history stay attached to it. Retired means kept, not offered - it leaves the
  general channel list and the **Random station** draw, because a channel the
  catalog no longer publishes must not sit among current ones as if it were
  still available, and it stays exactly where you put it, in the pinned strip
  and inside its collections. A later update that lists the address again simply
  brings the channel back.
- Search from the title line at any time, and open the filter and sorting row
  only when you need it with **Filters and sorting**. The row starts hidden,
  remembers whether you left it open, and closes from its own button without
  touching your filters; **Clear** resets the filters and keeps the row open and
  your search text intact. While the row is hidden, the button is marked and its
  tooltip counts the filters still narrowing the catalog. The broadcast-language
  filter leads with your interface language and its regional variants, and still
  starts on **All**.
- Narrow the list to one **topic** - the catalog's own set of station topics,
  from News and Classical to Traffic cams. Topic names are shown in your
  interface language and sorted in its alphabet, while the catalog itself keeps
  the original English name, so a list exported from one language reads the same
  in another. A topic this version has not seen yet is shown as the catalog
  spells it rather than hidden. **General** covers about half the catalog, so it
  sits at the end of the list, below the topics that actually narrow it.
- Reach the actions you use rarely from one **Operations** menu in the header:
  always on top, refresh previews (in grid mode), **Random station**, history,
  add stream, and **Import channels from the internet**.
- Keep the main window or video player independently always on top, and expand
  video to a borderless full screen with the button or `F11` (`Esc` exits). The
  player's three-dot actions menu keeps its own always-on-top switch alongside
  pinning and adding the stream to an existing or new collection. In either
  windowed or full screen mode, the player's control panel disappears after ten
  seconds without input; click the video to bring it back without interrupting
  playback. The player is a top-level window in its own right: minimizing or
  restoring the catalog no longer takes the picture with it, while quitting the
  catalog still closes every player.
- Switch between the list and a persisted visual grid with one header button that
  names the mode you are switching to. The grid captures visible HTTP(S) video
  previews, up to four at a time, and caches the frames on disk within a 150 MB
  budget.
- Recognise a channel the catalog gave no icon instead of reading it as a failed
  load. Most published rows carry no icon at all, and the empty square they used
  to leave looked like a picture that never arrived. Such a channel now shows a
  placeholder made from its own data - initials taken from the title, a
  background colour worked out from that same title, so one station looks the
  same on every launch and on every machine, and the two-letter country code
  where the row carries one. It appears in the list and on the grid tile, in the
  light and the dark theme alike; where a captured preview frame exists, the
  frame is still what you see.
- Open Settings to pick the interface language (**Language**, the first tab,
  marked with a globe); choose Very Small, Small, Medium, or Large stream tiles and
  disable automatic thumbnail updates (**Grid**); keep the computer awake, show system
  media controls, pick the video backend, and choose the folder for saved frames
  (**Playback**); and read the `YY.MMDD.HHmm` version and open the instruction,
  project, website, privacy, and author pages (**About**). The Settings window
  can be resized, and a tab taller than the window scrolls instead of clipping.
- Save the frame you are watching from the player's camera button: a JPEG named
  `Channel_YYYYMMDD-HHmmss` lands in the folder set on the **Playback** tab, or in
  Downloads when that is empty, and the same frame becomes the channel icon.
- Answer a failed stream from the failure dialog - **Retry**, **Copy report**,
  **Keep**, or remove it: a catalog channel is hidden and a channel of your own is
  deleted after a confirmation. Hidden catalog channels survive a refresh and come
  back from **Hidden** in Settings, on the **Playlists (M3U)** tab.
- Add a stream manually and keep local playback outcome marks.
- Ask **About channel**, from a channel's three-dot menu or from the player's
  actions menu, to see one page of everything known about it: what the channel is
  and where it came from, what the catalog claims about it, and what its stream is
  actually sending - video and audio format, picture size, frame rate, sound
  channels, sample rate and the measured data rate. Opening the window connects to
  the stream once to measure it, unless that channel is already playing, in which
  case the player already knows and nothing is opened. **Copy all** puts the whole
  list on the clipboard.
- Reopen a channel from a private **Recently played** history of the last 100
  channels you played, with the last observed now-playing text when a station
  provides it. History is local only, never uploaded, and cleared on demand;
  a channel you removed stays as a non-playable label.
- Import channels from a local `.m3u`/`.m3u8` file or an HTTP(S) playlist URL as
  `IMPORTED` rows, with an atomic preview of new, duplicate, invalid, and skipped
  counts before applying; HLS media manifests import nothing and explain why.
- Export your added (`MANUAL`/`IMPORTED`) channels, or just the pinned ones, to a
  UTF-8 M3U file, with a warning before writing any credential-bearing URL.
- Recommend a single channel as an ordinary chat message: **Copy share text** in a
  channel's actions menu puts one short line on the clipboard, such as
  `SPCH1 https://example.test/live`, which you paste into Telegram or anywhere else.
  The recipient chooses **Paste channel** - from the operations menu, or from the
  empty-catalog panel on a fresh install - reads what it found, and confirms before
  anything is added as an `IMPORTED` row. The line carries the address and nothing
  else: the title is derived from the address, so a password or token inside an
  address is visible to everyone who receives your message, and copying such a
  channel asks first. A channel you already have is not added twice - the app takes
  you to it, and offers to restore it if you had hidden it.
- Delete every downloaded catalog stream in one confirmed action from the
  **Playlists (M3U)** tab in Settings and keep only your own `MANUAL`/`IMPORTED`
  channels; **Import channels from the internet** downloads them again whenever
  you want them back.
- Switch the complete interface between thirteen languages from the **Language**
  tab in Settings - the first one, marked with a globe - including right-to-left
  layout for Arabic and Urdu; the choice is restored on the next launch, and the
  first launch follows Windows.
- Choose whether the interface follows the Windows colour theme, stays light,
  or stays dark from the **Grid** tab in Settings; following Windows is the
  default, an explicit choice is restored on the next launch, and the system
  choice updates in the same session when Windows changes.
- Group channels into local named collections, browse one collection at a time
  from the catalog filters, and manage them without touching pins or the catalog.
- Stop the sound without giving up the station: the bottom bar's audio button is
  a two-state transport, so **Stop audio** ends the session but keeps the station
  current and turns into **Resume audio**, which opens it again at the live edge.
  The volume slider and the sleep timer stay on the bar instead of disappearing,
  and pausing from the Windows media flyout leaves the bar in exactly the same
  state. A real stop - clicking the playing station in the list, **Stop** in the
  flyout, or starting another station - still clears the station and the controls
  with it.
- Set a sleep timer for inline radio - 15/30/45/60 minutes or a clock time - and
  watch the remaining time count down next to **Stop audio**; it survives a
  station switch and ends the session once when it expires.
- Let the catalog choose: **Random station** in the **Operations** menu draws one
  radio station from the whole catalog and plays it. The draw ignores the current
  search, the open facets and the active collection, and it never offers a
  channel you hid, a video stream or an RTSP address. A station that refuses, or
  that connects and stays silent for ten seconds, is dropped for the next draw
  with no dialog to dismiss; after five such stations in a row the hunt stops and
  says so on the status line. Nothing in the list moves - no scroll, no filter
  reset - and pressing the command again restarts the hunt instead of starting a
  second one beside it. A station that does start plays like any other: history,
  the Windows media flyout, the sleep timer and resume on startup all apply. The
  same command sits on the compact radio panel below.
- Shrink the catalog to a compact radio panel while a station is on. The button
  next to the transport hides the catalog and leaves a small window that stays
  above other programs and carries the station, the current track, the volume,
  the transport, the sleep timer with its countdown, and **Random station**. The
  two views are one application - one taskbar button, one Alt+Tab entry, one
  sound - and everything you change in one is what the other shows. Stopping the
  radio leaves the panel where it is instead of throwing the catalog back over
  your work, and so does a station that drops out. Drag it where you like: a
  panel pushed past an edge, or left on a monitor that is then switched off,
  comes back inside a screen you can see. Going back restores the full window
  exactly as you left it, scroll position, filter and selection included. The
  panel is a mode for the session: the next launch always opens the catalog.
- Store catalog state, manual entries, pins, collections, hidden catalog
  channels, listening history, cached preview frames, and the diagnostic
  logs of the last ten launches under `%LOCALAPPDATA%\StreamsPlayer` -
  `Current.log` for the running session, `Session-<date>-<time>.log` for the
  nine before it.
- Report a problem with **Send logs to the author** in the **About** tab of
  Settings: it packs those diagnostic logs plus a short summary of your app
  version, Windows version and settings into one archive in the **Saved files
  folder** (Downloads by default, configurable in Playback settings), then opens
  your mail program with the message prepared. Its confirmation shows the complete
  path and can open that folder when you ask. Nothing is sent automatically - you
  attach the archive and press Send. The logs name the streams that were played, so
  send them only if you are comfortable sharing that.

Audio playback uses WPF `MediaElement`; video and RTSP use the bundled LibVLC
runtime with a 15-second live buffer - 4 seconds when a stalled stream is
re-opened - and visible buffering progress. Grid preview capture also uses
LibVLC. Live playback recovers from transient network failures and silent
stalls - including a stream that stops sending while still reporting that it is
playing, which is detected and re-opened instead of leaving a frozen picture -
with a bounded retry policy, showing a distinct Reconnecting state and the
failure dialog above when recovery is exhausted. Whenever the picture is not
running, the player writes the reason over the video - connecting, signal lost,
reconnecting with the attempt count, or switching quality - and that caption is
placed so that it stays readable after the control panel auto-hides. An HLS
stream that offers more than one quality is watched while it plays: repeated
buffer starvation settles the ceiling one rung lower, the player probes back up
when the connection allows it again, and the ceiling it settled on is remembered
per channel, so the next session opens there rather than measuring from scratch.
A station that publishes ICY metadata shows its current track beside the station
name, in the player window under the channel name, in **Recently played**, and
in the Windows media session when system media controls are on. The video player
offers audio-track and subtitle selection whenever a stream carries more than
one.

Video and RTSP can also run on a second, experimental engine, FlyleafLib, chosen
in **Settings → Playback** as a fallback for a stream that misbehaves under VLC.
It needs FFmpeg libraries that are not shipped with the application; the same
screen states whether they are installed and downloads them on request, and VLC
stays the default until you change it.

## Run from source

```powershell
./build.ps1 -Test -Deploy:$false
./run.ps1
```

`build.ps1` deploys by default: without `-Deploy:$false` it forces a Release build and copies a published
executable into the author's local folders. `run.ps1` never does that.

Or start the desktop app directly:

```powershell
dotnet run --project src/StreamsPlayer.App
```

## Launch a stream

Use a direct URL without downloading the catalog:

```powershell
StreamsPlayer.exe --url "https://example.test/live"
```

For a saved channel, select it in the catalog, open Settings, and use **Copy
command** or **Create desktop shortcut**. These entries use the channel's
persisted GUID:

```powershell
StreamsPlayer.exe --id "channel-guid"
```

An ordinary launch without arguments starts nothing. Turn on **Resume playback on startup** on the
Playback tab in Settings and a launch brings back whatever was playing when you last closed the app -
the radio station and every player window alike. It is off by default.

## Development

| Area | Purpose |
| --- | --- |
| `src/StreamsPlayer.Core` | Platform-neutral catalog contracts, parsing, merge, and local persistence. |
| `src/StreamsPlayer.App` | WPF desktop application. |
| `tests/StreamsPlayer.Core.Tests` | Unit and contract tests. |
| `tools/StreamsPlayer.CatalogHarness` | Live stream-bank diagnostic harness. |
| `docs/` | GitHub Pages product site and specifications. |

Run the release-style local check:

```powershell
./scripts/check.ps1
```

Run the live-bank harness:

```powershell
dotnet run --project tools/StreamsPlayer.CatalogHarness -- artifacts/favicon-sample.png
```

`build.ps1` is a local Windows-app build flow: it creates a self-contained EXE
and places it in the local app folders. It does not commit, push, tag, or publish
a release. Use `-Deploy:$false` when only the ordinary solution build is needed.

## Privacy

STREAMS Player does not require an account and includes no advertising, analytics,
telemetry, or author-operated service. Network access happens when you explicitly
refresh the public catalog, play a selected stream, keep Grid mode active while
STREAMS Player refreshes visible video previews, or accept the optional preview
pack it offers after a catalog update. Local data leaves your device only if you
send it yourself - **Send logs to the author** prepares an archive and a message in
your own mail program, and never sends anything on its own. See the
[privacy page](https://serzhyale.github.io/StreamsPlayer/privacy.html) for details.

## Ownership and license

STREAMS Player is independently owned and authored by
[Serhii Zhyhunenko / SerZhyAle](https://github.com/SerZhyAle).

Licensed under the [MIT License](LICENSE).
