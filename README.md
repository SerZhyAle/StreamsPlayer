<p align="center">
  <img src="docs/assets/streamplayer-icon-256.png" alt="StreamPlayer icon" width="112">
</p>

<h1 align="center">StreamPlayer</h1>

<p align="center">Internet radio, live video, and RTSP for Windows.</p>

<p align="center">
  <a href="https://serzhyale.github.io/StreamPlayer/">Website</a> ·
  <a href="https://github.com/SerZhyAle/StreamPlayer">Source</a> ·
  <a href="https://serzhyale.github.io/StreamPlayer/privacy.html">Privacy</a>
</p>

<p align="center">
  <strong>Language:</strong>
  <a href="README.md">English</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.uk.md">Українська</a>
</p>

> **Release status:** StreamPlayer is in active development. Portable ZIP,
> Microsoft Store, and winget channels are planned but are not published
> downloads yet.

## A calm player for the stream in front of you

| Find a channel | Keep your choices | Play the right media |
| --- | --- | --- |
| Browse a curated catalog and filter by category, language, country, or media type. | Search, sort, pin, and add your own streams without an account. | Listen to radio in the main window or open live video and RTSP in a focused player window. |

StreamPlayer is an independent Windows desktop application for internet radio,
live video, and RTSP channels. It consumes the published FastMediaSorter stream
bank as an external data contract; it does not share FastMediaSorter application
code or features.

## What it does

- Refresh the catalog only when you choose to. There are no surprise background
  catalog downloads.
- Read the published stream bank with RFC-4180 CSV, ZIP entry-order, and optional
  favicon-atlas checks.
- Protect your `MANUAL` and `IMPORTED` rows while updating the catalog by URL.
- Filter, search, sort, and pin radio, video, and RTSP streams.
- Switch the complete interface between English and Russian from the top bar;
  the language choice is restored on the next launch.
- Keep the main window or video player independently always on top, and expand
  video to a borderless full screen with the button or `F11` (`Esc` exits).
- Switch to a persisted visual grid that captures visible HTTP(S) video previews
  sequentially and keeps the latest 64 frames locally.
- Open compact Settings to choose Small, Medium, or Large stream tiles, disable
  automatic thumbnail updates, view the `YY.MMDD.HHmm` version, and open project,
  privacy, instruction, and author pages.
- Add a stream manually and keep local playback outcome marks.
- Store catalog state, manual entries, pins, and the current-session diagnostic `Current.log` under `%LOCALAPPDATA%\StreamPlayer`.

Audio playback uses WPF `MediaElement`; video and RTSP use the bundled LibVLC
runtime with a target 10-second live buffer and visible buffering progress. Grid
preview capture also uses LibVLC. ICY metadata, adaptive recovery, playlist-import
UI, and advanced player controls remain later milestones.

## Run from source

```powershell
./build.ps1 -Test
./build.ps1 -Run
```

Or start the desktop app directly:

```powershell
dotnet run --project src/StreamPlayer.App
```

## Launch a stream

Use a direct URL without downloading the catalog:

```powershell
StreamPlayer.exe --url "https://example.test/live"
```

For a saved channel, select it in the catalog, open Settings, and use **Copy
command** or **Create desktop shortcut**. These entries use the channel's
persisted GUID:

```powershell
StreamPlayer.exe --id "channel-guid"
```

An ordinary launch without arguments resumes the last selected saved channel.

## Development

| Area | Purpose |
| --- | --- |
| `src/StreamPlayer.Core` | Platform-neutral catalog contracts, parsing, merge, and local persistence. |
| `src/StreamPlayer.App` | WPF desktop application. |
| `tests/StreamPlayer.Core.Tests` | Unit and contract tests. |
| `tools/StreamPlayer.CatalogHarness` | Live stream-bank diagnostic harness. |
| `docs/` | GitHub Pages product site and specifications. |

Run the release-style local check:

```powershell
./scripts/check.ps1
```

Run the live-bank harness:

```powershell
dotnet run --project tools/StreamPlayer.CatalogHarness -- artifacts/favicon-sample.png
```

`build.ps1` is a local Windows-app build flow: it creates a self-contained EXE
and places it in the local app folders. It does not commit, push, tag, or publish
a release. Use `-Deploy:$false` when only the ordinary solution build is needed.

## Privacy

StreamPlayer does not require an account and includes no advertising, analytics,
telemetry, or author-operated service. Network access happens when you explicitly
refresh the public catalog, play a selected stream, or keep Grid mode active while
StreamPlayer refreshes visible video previews. See the
[privacy page](https://serzhyale.github.io/StreamPlayer/privacy.html) for details.

## Ownership and license

StreamPlayer is independently owned and authored by
[Serhii Zhyhunenko / SerZhyAle](https://github.com/SerZhyAle).

Licensed under the [MIT License](LICENSE).
