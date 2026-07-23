# Publishing StreamsPlayer to the Microsoft Store (MSIX)

Reusable, StreamsPlayer-specific playbook, adapted from the CyrFlip/FastMediaSorter
MSIX pattern. The product is already reserved in Partner Center; this document is
the step-by-step to build, submit, and update.

## Reserved identity (permanent — supply on every submission)

| Field | Value |
| --- | --- |
| Store title (reserved app name) | `Streams Player` |
| In-app / docs wordmark | `STREAMS Player` |
| `Package/Identity/Name` | `SZA.StreamsPlayer` |
| `Package/Identity/Publisher` | `CN=F98ACEDB-1E22-4C39-AF63-F9FCFE807DCD` |
| `Package/Properties/PublisherDisplayName` | `SZA` |
| Package Family Name | `SZA.StreamsPlayer_fdk7e19xt9z9j` |
| Store ID | `9NBTD5SXB8TB` |
| Store link (after live) | `https://apps.microsoft.com/detail/9NBTD5SXB8TB` |

`msix/build-msix.ps1` already defaults to the three identity values, so a plain build is correctly identified.

## Why this path

Microsoft re-signs the MSIX at certification, so no paid code-signing certificate
is needed, and a Store-signed build avoids SmartScreen/AV false positives. The
individual developer account is free.

---

## Phase 1 — Pre-flight (code)

- Release-parity gate green: `./scripts/check.ps1` → Release build + `dotnet test` (expected: 0 errors, all tests pass).
- Version set in `Directory.Build.props` (`YY.MMDD.HHmm`), later than every published version. The MSIX version is remapped to `YY.MMDD.HHmm.0` by the build script.
- Local state already lives under `%LOCALAPPDATA%\StreamsPlayer`; catalog refresh is explicit (no background downloads). No MSIX file/registry-virtualization changes are required — the app writes only to its own per-user profile and does not rely on other processes reading those files.

## Phase 2 — Build the package

```powershell
# Uses the reserved identity by default. Unsigned = Store-ready (Microsoft signs at certification).
./msix/build-msix.ps1

# Local sideload test only (never upload a self-signed package):
./msix/build-msix.ps1 -SelfSign
```

Output: `msix/dist/StreamsPlayer-<version>-windows-x64.msix`.
The package bundles `LICENSE.txt` (MIT) **and** `THIRD-PARTY-NOTICES.txt`
(LibVLC/VLC LGPL+GPL, FFmpeg/Flyleaf) — required because the app redistributes
LGPL/GPL native media libraries. Do not remove the notices file.

Requires the Windows SDK (`makeappx.exe`, and `signtool.exe` for `-SelfSign`):
`winget install Microsoft.WindowsSDK`.

## Phase 3 — Verify locally

`./msix/build-msix.ps1 -SelfSign` prints the `Import-Certificate` (run as admin)
and `Add-AppxPackage` commands. Install, launch from the Start menu, and confirm:
catalog refresh, audio playback, Grid thumbnails, the video player (always-on-top,
F11/Escape fullscreen), and EN/RU switching.

## Phase 4 — Listing materials (all present in this repo)

| Item | Source |
| --- | --- |
| Copy deck (EN + RU: description, features, keywords, runFullTrust justification, certification notes) | `msix/store-listing.md` |
| Import-ready listing CSV (EN + RU columns) | `msix/store-listing-import.csv` — see "Listing import via CSV" below |
| Screenshots (composed, 1366×768, EN + RU) | `assets/store/screenshot-{en,ru}-1366x768.png` — regenerate with `tools/store/make-store-images.ps1` |
| Real in-app screenshots (recommended to add before submit) | `tools/store/capture-app.ps1 -Name <shot>` |
| Banner / social preview | `assets/store/banner-1280x360.png`, `assets/store/social-preview-1280x640.png` |
| Privacy policy | `docs/privacy.html` → `https://serzhyale.github.io/StreamsPlayer/privacy.html` |
| Category | Primary **Entertainment**, secondary **Music** |
| Price | Free (Retail price dropdown) |

**Screenshots — do this before submitting:** the composed cards satisfy the
minimum, but a media player is far stronger with genuine captures. Launch the app,
refresh the catalog, and run `tools/store/capture-app.ps1` for: (1) catalog List
mode, (2) Grid mode with thumbnails, (3) the video player with controls, (4)
Settings. Upload the real shots; keep 1–2 composed cards only if you want a titled
lead image.

### Listing import via CSV

`msix/store-listing-import.csv` holds both listings (columns `Field, ID, Type,
default, en-us, ru-ru`) so you can populate EN and RU in one go via **Import
listings → Upload .csv** instead of typing into Partner Center.

Partner Center requires the `Field`, `ID`, and `Type` columns to match its own
generated template exactly, and the `ID` numbers are account-specific and not
documented. Two ways to use the file:

A direct upload of `store-listing-import.csv` is **rejected** ("The ID column
contains incorrect entries") — Partner Center requires its own `ID` values. Use
the merge script instead:

1. App overview → **Store listings → Export listing** → save the file (e.g.
   `tmp/exported-listing.csv`). Its language columns are `en-us` and `ru`.
2. Run:
   ```powershell
   pwsh -NoProfile -File tools/store/merge-listing-csv.ps1 `
     -Template tmp/exported-listing.csv `
     -Out msix/store-listing-import.filled.csv
   ```
   This keeps the template's `Field`/`ID`/`Type` untouched and fills `en-us` + `ru`
   from `store-listing-import.csv` (the content source of truth).
3. **Import listings → Upload .csv** → `msix/store-listing-import.filled.csv`.

Notes:
- The file is UTF-8 with BOM (keeps Cyrillic intact in Excel). Keep that encoding.
- `ReleaseNotes` is intentionally blank (first submission).
- **Screenshots are not in the CSV.** Either upload them in the Partner Center UI,
  or use folder import: put the CSV + `assets/store/*.png` in one folder, add
  `DesktopScreenshot1…` rows whose value is `<folder>/screenshot-en-1366x768.png`,
  and choose **Upload folder**.
- Review the RU body: it refers to the app as "Трансляции" (a translation). The
  Store *title* is "Streams Player" regardless; change the RU prose only if you
  want the Latin wordmark there too.

## Phase 5 — Age rating (IARC)

Complete the IARC questionnaire **fresh** for this app. The SZA portable rating ID
used by FastMediaSorter does **not** transfer here: StreamsPlayer can open arbitrary
third-party live audio/video URLs, which changes the questionnaire answers (uncurated
online content). Answer honestly: no accounts, no purchases, no ads, no user-to-user
content publishing; the app can display uncontrolled third-party streams.

## Phase 6 — Content-policy note (StreamsPlayer-specific)

Apps that open third-party streams draw extra review under the Store's
infringing-content policy. Pre-empt it:

- Frame the app as an **internet-radio / live-stream catalog player** (it is), not a
  piracy tool. The listing already leads with the curated catalog.
- The keyword `IPTV player` in `msix/store-listing.md` is the most likely trigger.
  Consider dropping it or replacing it with `internet radio` / `stream player` if a
  reviewer pushes back.
- Paste the runFullTrust justification from `msix/store-listing.md` verbatim (it
  explains the LibVLC media components, the explicit-refresh network model, and the
  absence of accounts/ads/telemetry, with the source link).

## Phase 7 — Submit / update in Partner Center

1. Partner Center → **Apps and games → Streams Player** (Store ID `9NBTD5SXB8TB`).
2. **Packages** → upload `msix/dist/StreamsPlayer-<version>-windows-x64.msix`.
3. **Store listings** → English (paste from `msix/store-listing.md`); add Russian via
   *Manage additional languages* and paste the RU copy. Upload screenshots.
4. **Properties** → category Entertainment; **Age ratings** → complete IARC.
5. **Pricing and availability** → Free, markets.
6. Submit for certification (a few business days). For an update, *Create new
   submission*, replace the package (same identity, higher version), refresh copy, submit.

> The `msstore` CLI cannot automate submissions on an individual account (no Azure AD
> org). Use the Partner Center web UI.

---

## winget channel (separate from the Store)

Identifier `SerZhyAle.StreamsPlayer`. Gated on a public GitHub Release (ZIP + SHA256),
so it can only be updated **after** an approved release. Preferred flow:

```powershell
wingetcreate update SerZhyAle.StreamsPlayer `
  --version <YY.MMDD.HHmm> `
  --urls https://github.com/SerZhyAle/StreamsPlayer/releases/download/v<version>/StreamsPlayer-<version>-windows-x64.zip `
  --submit
```

`wingetcreate` recomputes the SHA256 and opens the PR to `microsoft/winget-pkgs`.
The manifest templates in `winget/templates/` remain the source of truth for fields
not derived automatically. See `winget/README.md`.
