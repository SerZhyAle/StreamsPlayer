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

> **Release status:** STREAMS Player is released and free. Run
> `StreamsPlayer-<version>-windows-x64-setup.exe` from the
> [latest GitHub Release](https://github.com/SerZhyAle/StreamsPlayer/releases/latest)
> - it installs for the current user and needs no administrator rights - or get
> it on the [Microsoft Store](https://apps.microsoft.com/detail/9NBTD5SXB8TB),
> take the portable ZIP from the same release, or use
> `winget install SerZhyAle.StreamsPlayer`. No account required, no ads, no
> telemetry; the source is MIT.
>
> The installer, the ZIP and the winget package are not code-signed, so Windows
> may show **"Windows protected your PC"** the first time you run them -
> [what that screen means and what to click](https://serzhyale.github.io/StreamsPlayer/trust.html).
>
> To update or uninstall, close STREAMS Player first - the setup asks you to if it
> is still open. A silent run (`winget`, `/VERYSILENT`) does not ask: it stops
> with exit code 1 and changes nothing.

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
  under **Library** in **Settings** (or from **Library settings** in the **Operations** menu). It only adds and updates channels
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
- Switch between watching and listening from the title line itself: three small
  buttons beside **Filters and sorting** are that same media filter in one
  click - press **video** to keep video and RTSP only, **audio** to keep radio
  only, **my channels** to keep only the ones you added or imported, and press
  the same button again for everything. A pressed button is lit and an
  unpressed one is dimmed; the three stay in the title line whether the filter
  row is open or closed, narrow the pinned strip along with the list, and are
  still in force after a restart. They are the row's own **Media** filter, so
  the two can never disagree.
- Narrow the list to one **topic** - the catalog's own set of station topics,
  from News and Classical to Traffic cams. Topic names are shown in your
  interface language and sorted in its alphabet, while the catalog itself keeps
  the original English name, so a list exported from one language reads the same
  in another. A topic this version has not seen yet is shown as the catalog
  spells it rather than hidden. **General** covers about half the catalog, so it
  sits at the end of the list, below the topics that actually narrow it.
- Hide adult channels from the catalog, the filters and the **Random station**
  draw at any time using the **Hide adult channels** option in Settings.
- Reach the actions you use rarely from one **Operations** menu in the header:
  always on top, refresh previews (in grid mode), **Random station**, history,
  add stream, **Paste channel**, **Library settings**, and, set apart at the end,
  **Import channels from the internet**.
- Keep the main window or video player independently always on top, and expand
  video to a borderless full screen with the button or `F11` (`Esc` exits). The
  player's three-dot actions menu keeps its own always-on-top switch alongside
  pinning and adding the stream to an existing or new collection. In either
  windowed or full screen mode, the player's control panel disappears after four
  seconds without input; click the video to bring it back without interrupting
  playback, or press the pin on the panel to keep it on screen - the pin applies
  to every player window and is remembered. The player is a top-level window in its own right: minimizing or
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
- Open Settings from the gear button in the header (or from **Library settings** in the **Operations** menu)
  to configure preferences and run maintenance actions. Six pages are listed down the side of the window,
  each with its own coloured glyph; the page you are on carries an accent bar, and its title and a short
  description head the page. Related settings sit in named groups that you collapse one at a time, or all
  at once with the two buttons beside the search box. Each setting shows its name and a short hint with its
  control beside them, and a checkbox stays next to its caption. Type in **Search settings** to find a
  setting by its name or its hint: every result names its page and group, and choosing one - a click, or
  `Down` and `Enter` - opens that page, expands the group and puts the cursor on the setting. The pages:
  - **General**: choose the interface language (**Language**, marked with a globe; 13 languages, endonyms only), colour theme (**Follow system**, **Light**, or **Dark**), stream tile size (**Very Small**, **Small**, **Medium**, or **Large**), and animated backdrop;
  - **Library**: hide adult channels, toggle automatic thumbnail updates, import external catalog archives, apply the built-in channel snapshot, cleanly delete downloaded or imported catalogs with a confirmation, import and export M3U playlists, manage hidden channels, and configure the TV schedule;
  - **Playback**: keep the computer awake, show system media controls, resume playback on startup, choose the video engine (**VLC** or experimental **FlyleafLib**), and download or remove FlyleafLib components;
  - **Audio**: choose the audio output device and the channel mode (stereo, reversed stereo, left, right, or Dolby Surround);
  - **Files**: choose folders for saved frames, video recordings, and radio recordings, with quick buttons to browse, reset to default, or open the folder;
  - **About**: view product details and `YY.MMDD.HHmm` version, view the MIT licence, open documentation, source, website, privacy, and author links, and generate diagnostic log bundles with **Send logs to the author**.
  Settings apply immediately upon touch, and closing the window (or `Esc`; in the search box the first `Esc` clears the search) is the
  sole exit. Settings reopens on the page you left, with the same groups open, scrolled back to where you
  were, and at the size and position you gave it.
- Save the frame you are watching from the player's camera button: a JPEG named
  `video_frame_YYMMDD_HHmmss_<channel>` lands in the frames folder set on the **Files** page,
  or in the Frames folder inside Pictures when that is empty, and the same frame becomes the
  channel icon. A second frame in the same second gets ` (2)`; a folder that cannot be written
  sends the file to the next one - the default, then Downloads - and the message says where it went.
- Record a live broadcast with the **Record** button (or press `R` in the video player):
  losslessly captures the active live video or radio stream directly to a media file without
  interrupting playback, and shows the saved file name on stop. A video recording is named
  `stream_video_YYMMDD_HHmmss_<channel>` and goes to the Recordings folder inside Videos, a radio
  recording `stream_audio_..` to the Recordings folder inside Music, unless you chose other
  folders on the **Files** page.
  A video recording survives reconnects - each connection becomes its own file and all of
  them are reported at the end; a radio recording is named after the format the station
  really sends and follows a `.pls`/`.m3u` link to the stream, and when the station drops
  the connection you are told at once what was saved and how long it runs. A FastMediaSorter
  broadcast cannot be recorded (it allows only the one connection playback uses). Quitting
  while recording is safe: the app waits for the file to reach the recordings folder, and
  anything it could not wait for is handed over there and announced at the next start.
- Answer a failed stream from the failure dialog - **Retry**, **Copy report**,
  **Keep**, or remove it: a catalog channel is hidden and a channel of your own is
  deleted after a confirmation. Hidden catalog channels survive a refresh and come
  back from **Hidden** on the **Library** page in **Settings**.
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
- See what is on a TV channel now and next from a TV schedule you choose: paste
  the address of an XMLTV guide (`.xml` or `.xml.gz`, for example one from the
  iptv-org/epg project) under **TV schedule** on the **Library** page in **Settings** and press **Download
  schedule**. The channel list then shows the current programme under each
  matched channel, and the player shows the current and next programme with
  their times, in your local time. Channels are matched by exact name; a name the
  guide uses for more than one channel matches nothing, and **TV schedule
  channel..** in a channel's three-dot menu binds it by hand or turns its
  schedule off. The guide is downloaded only when you press the button, kept for
  36 hours ahead within a fixed size limit, never updated in the background, and
  **Remove schedule** deletes it. A channel without a schedule looks and plays
  exactly as before.
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
- Listen to a live audio broadcast from a FastMediaSorter phone or watch. Import it
  three ways: paste its barcode text (`FMSBCAST1:` ..) or its Android share link
  with **Paste channel** - scan the barcode with any scanner app, since StreamsPlayer
  has no camera and takes only the decoded text; open a `.fmsbcast` file from the
  playlist import file dialog in **Settings (Library)**; or drag
  one `.fmsbcast` file onto the main window. The app shows the title and address
  and asks before adding; files over 64 KiB are refused. The broadcast becomes an
  `IMPORTED` row marked **Live**, and importing again from the same device updates
  that row - address, port, title - instead of adding a duplicate, so pins,
  collections and history stay; a catalog refresh never removes it. Only audio
  broadcasts (`AUDIO_ONLY`) play today: a video mode is refused with an
  explanation, and a descriptor from a newer FastMediaSorter asks you to update
  StreamsPlayer. The connection is direct over the same local network (LAN) as
  the device, never through a server. Playback takes a separate low-latency path
  aimed at sound within about a second and no more than two seconds behind the
  device; after a dropout it reconnects a bounded number of times and rejoins the
  live moment instead of replaying what was missed. A watch allows only four
  listeners, so the app opens exactly one connection per listening session and
  never probes the address - **About channel** does not measure it either. A
  watch that already has four listeners gets a message saying so, with no retry;
  a device that stops broadcasting gets a message that it stopped or is no longer
  reachable instead of an endless "Connecting". The app does not amplify a quiet
  microphone: the volume is whatever the device sends.
- Delete every downloaded catalog stream in one confirmed action from the
  **Library** page in **Settings** and keep only your own `MANUAL`/`IMPORTED`
  channels; **Import channels from the internet** downloads them again whenever
  you want them back.
- Switch the complete interface between thirteen languages from the **Language**
  selector on the **General** page in Settings - marked with a globe - including right-to-left
  layout for Arabic and Urdu; the choice is restored on the next launch, and the
  first launch follows Windows.
- Choose whether the interface follows the Windows colour theme, stays light,
  or stays dark from the **General** page in Settings; following Windows is the
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
  channel you hid, an adult channel while adult channels are hidden, a video
  stream or an RTSP address. A station that refuses, or
  that connects and stays silent for ten seconds, is dropped for the next draw
  with no dialog to dismiss; after five such stations in a row the hunt stops and
  says so on the status line. Nothing in the list moves - no scroll, no filter
  reset - and pressing the command again restarts the hunt instead of starting a
  second one beside it. A station that does start plays like any other: history,
  the Windows media flyout, the sleep timer and resume on startup all apply. The
  same command sits on the compact radio panel below.
- The playing station carries a quiet animated background - drifting lines and
  particles, the same motion as FastMediaSorter's audio player - on its card in
  the list, on its tile in the grid (the station picture moves to a plate in the
  middle) and in the compact radio panel below. It is dimmed so the text stays
  readable, follows the light and dark theme, freezes on its last frame when you
  stop, draws nothing while it is out of sight, and pauses under Windows energy
  saver. Turn it off in **Settings** with **Animated background behind the
  playing station**.
- Shrink the catalog to a compact radio panel while a station is on. The button
  next to the transport hides the catalog and leaves a small captionless strip:
  its top line names the station and the current track, and the line below
  carries the volume, the sleep timer with its countdown, previous and next
  station, **Random station**, **Record**, stop and resume, and the panel's own
  buttons - always on top (on until you switch it off), back to the full window,
  minimize, and close. Any empty spot drags it. The two views are one application - one taskbar button, one Alt+Tab entry, one
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
- Report a problem with **Send logs to the author** on the **About** page in **Settings**:
  it packs those diagnostic logs plus a short summary of your app
  version, Windows version and settings into one archive in the **frames folder**
  when you chose one on the **Files** page, or in Downloads, then opens
  your mail program with the message prepared. Its confirmation shows the complete
  path and can open that folder when you ask. Nothing is sent automatically - you
  attach the archive and press Send. The logs name the streams that were played, so
  send them only if you are comfortable sharing that.

Radio plays through the bundled LibVLC runtime in an audio-only engine, and a
FastMediaSorter broadcast takes its own low-latency path; video and RTSP use
LibVLC as well, with a 15-second live buffer - 4 seconds when a stalled stream is
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
It needs FFmpeg libraries that are not shipped with the application; that page
states whether they are installed, downloads them on request, and VLC
stays the default until you change it.

## Controls

The same glyph and the same name everywhere in the portfolio - in the app, on the site and here (the shared `ICON-SET` vocabulary).

| | Control |
| --- | --- |
| <img src="docs/assets/glyphs/media.play.svg" width="16" height="16" alt=""> <img src="docs/assets/glyphs/media.stop.svg" width="16" height="16" alt=""> | **Play** / **Stop** - a channel row, the radio bar, the compact panel; the live button shows what a click will do |
| <img src="docs/assets/glyphs/media.previous.svg" width="16" height="16" alt=""> <img src="docs/assets/glyphs/media.next.svg" width="16" height="16" alt=""> | **Previous station** / **Next station** - the compact panel |
| <img src="docs/assets/glyphs/media.random.svg" width="16" height="16" alt=""> | **Random station** - the **Operations** menu and the compact panel |
| <img src="docs/assets/glyphs/media.record.svg" width="16" height="16" alt=""> | **Record** - the radio bar, the compact panel, the video player (`R`) |
| <img src="docs/assets/glyphs/media.sleep-timer.svg" width="16" height="16" alt=""> | **Sleep timer** - next to **Stop audio** |
| <img src="docs/assets/glyphs/media.picture-in-picture.svg" width="16" height="16" alt=""> | The compact panel - the radio bar; the panel has its own way back to the full window |
| <img src="docs/assets/glyphs/media.fullscreen.svg" width="16" height="16" alt=""> <img src="docs/assets/glyphs/media.exit-fullscreen.svg" width="16" height="16" alt=""> | **Fullscreen** / **Exit fullscreen** - the video player (`F11`) |
| <img src="docs/assets/glyphs/media.mute.svg" width="16" height="16" alt=""> <img src="docs/assets/glyphs/media.volume.svg" width="16" height="16" alt=""> | **Mute** / **Unmute** - the video player |
| <img src="docs/assets/glyphs/nav.more.svg" width="16" height="16" alt=""> | **More actions** - a channel's menu; the **Operations** menu in the header |
| <img src="docs/assets/glyphs/app.settings.svg" width="16" height="16" alt=""> | **Settings** |
| <img src="docs/assets/glyphs/action.filter.svg" width="16" height="16" alt=""> <img src="docs/assets/glyphs/action.clear-filter.svg" width="16" height="16" alt=""> | **Filters and sorting** / **Clear** the filters |
| <img src="docs/assets/glyphs/view.list.svg" width="16" height="16" alt=""> <img src="docs/assets/glyphs/view.grid.svg" width="16" height="16" alt=""> | List view / grid view of the catalog |
| <img src="docs/assets/glyphs/action.pin.svg" width="16" height="16" alt=""> | **Pin** a channel to the top of the list |
| <img src="docs/assets/glyphs/action.refresh.svg" width="16" height="16" alt=""> | **Import channels from the internet** - load the shared catalog again |
| <img src="docs/assets/glyphs/action.import.svg" width="16" height="16" alt=""> <img src="docs/assets/glyphs/action.export.svg" width="16" height="16" alt=""> | Import / export a playlist - **Settings (Library)** |

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

For a saved channel, open its <img src="docs/assets/glyphs/nav.more.svg" width="16" height="16" alt=""> three-dot menu and use **Copy launch command** or
**Create desktop shortcut**. These entries carry the channel's persisted GUID and, when safe, its address,
so the channel is still found after a catalog refresh gives it a new GUID:

```powershell
StreamsPlayer.exe --id "channel-guid" --url "https://example.test/live"
```

If the address contains credentials or cannot fit safely in a launch command, the shortcut or copied
command carries only the GUID. The app tells you when this happens. That entry works only while the
channel remains in your library; its address is never placed in the shortcut or command line.
Recreate shortcuts made by older versions to remove any address they already stored.

An ordinary launch without arguments starts nothing. Turn on **Resume playback on startup** on the
Playback page in Settings and a launch brings back whatever was playing when you last closed the app -
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
telemetry, or author-operated service. Network access happens only on your
action: when you import the public catalog or accept the optional preview pack
it offers afterwards (both from GitHub); when you play, record or ask **About
channel** for a stream, or keep Grid mode active while visible video previews
refresh (all to that stream's own provider); when you listen to a
FastMediaSorter broadcast you imported (directly to that device on your local
network); and when you download the optional FFmpeg libraries in **Settings (Playback)**
(from a third-party GitHub project, `BtbN/FFmpeg-Builds`); and when you press
**Download schedule** in **Settings (Library)** (from the TV schedule address you entered
yourself). Local data leaves your device only if you
send it yourself - **Send logs to the author** prepares an archive and a message in
your own mail program, and never sends anything on its own. See the
[privacy page](https://serzhyale.github.io/StreamsPlayer/privacy.html) for details.

The optional Exchange source in Library settings connects to your own server over TLS. Enrollment sends your login, password or pairing code, device identity, product version and receiver capabilities. Later connections use the device identity and device token. It holds one control connection with keepalives and reconnects while enabled; disabling it closes the connection. Every certificate leaf needs your explicit approval, including after renewal. The password and pairing code are discarded. The account, token and certificate pin are protected for your Windows user in a separate file; removing the account forgets them. Diagnostic bundles omit that file and redact account values. Exchange connections begin only after your explicit source setup.

## Ownership and license

STREAMS Player is independently owned and authored by
[Serhii Zhyhunenko / SerZhyAle](https://github.com/SerZhyAle).

Licensed under the [MIT License](LICENSE).
