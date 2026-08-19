# Publishing StreamsPlayer to the Microsoft Store (MSIX)

Reusable, StreamsPlayer-specific playbook, adapted from the CyrFlip/FastMediaSorter
MSIX pattern. The product is already reserved in Partner Center; this document is
the step-by-step to build, submit, and update.

## Reserved identity (permanent - supply on every submission)

| Field | Value |
| --- | --- |
| Store title (reserved app name) | `Streams Player` |
| In-app / docs wordmark | `STREAMS Player` |
| `Package/Identity/Name` | `SZA.StreamsPlayer` |
| `Package/Identity/Publisher` | `CN=F98ACEDB-1E22-4C39-AF63-F9FCFE807DCD` |
| `Package/Properties/PublisherDisplayName` | `SZA` |
| Package Family Name | `SZA.StreamsPlayer_fdk7e19xt9z9j` |
| Store ID | `9NBTD5SXB8TB` |
| Store link (live) | `https://apps.microsoft.com/detail/9NBTD5SXB8TB` |
| IARC Global Rating ID | `7fd02683-3ef9-8606-8fb0-3f68aa55f8fc` |
| IARC rating date / storefront | `2026-07-23` · Microsoft |

`msix/build-msix.ps1` already defaults to the three identity values, so a plain build is correctly identified.

## Why this path

Microsoft re-signs the MSIX at certification, so no paid code-signing certificate
is needed, and a Store-signed build avoids SmartScreen/AV false positives. The
individual developer account is free.

---

## Phase 1 - Pre-flight (code)

- Release-parity gate green: `./scripts/check.ps1` → Release build + `dotnet test` (expected: 0 errors, all tests pass).
- Version set in `Directory.Build.props` (`YY.MMDD.HHmm`), later than every published version. The MSIX version is remapped to `YY.MMDD.HHmm.0` by the build script.
- Local state already lives under `%LOCALAPPDATA%\StreamsPlayer`; catalog refresh is explicit (no background downloads). No MSIX file/registry-virtualization changes are required - the app writes only to its own per-user profile and does not rely on other processes reading those files.

## Phase 2 - Build the package

```powershell
# Uses the reserved identity by default. Unsigned = Store-ready (Microsoft signs at certification).
# ALWAYS pass -Version on a release. Without it the script stamps the clock at the moment it runs,
# not the version being released, and you get a package whose name and Identity Version disagree with
# the tag, the GitHub Release and the winget manifest (observed on 26.0819.0156, which first built as
# 26.0819.0217). The argument takes the four-part form - the released YY.MMDD.HHmm plus ".0".
./msix/build-msix.ps1 -Version <YY.MMDD.HHmm>.0

# Ad-hoc local package, where a fresh clock stamp is exactly what you want:
./msix/build-msix.ps1

# Local sideload test only (never upload a self-signed package):
./msix/build-msix.ps1 -SelfSign -Version <YY.MMDD.HHmm>.0
```


Output: `msix/dist/StreamsPlayer-<version>-windows-x64.msix`.
The package bundles `LICENSE.txt` (MIT) **and** the repo-root `THIRD-PARTY-NOTICES.txt`
(LibVLC/VLC LGPL+GPL, FFmpeg/Flyleaf) - required because the app redistributes
LGPL/GPL native media libraries. Do not remove the notices file. The same file
ships inside the portable release zip, for the same reason - the obligation
belongs to the redistribution, not to one packaging format.

Requires the Windows SDK (`makeappx.exe`, and `signtool.exe` for `-SelfSign`):
`winget install Microsoft.WindowsSDK`.

## Phase 3 - Verify locally

`./msix/build-msix.ps1 -SelfSign` prints the `Import-Certificate` (run as admin)
and `Add-AppxPackage` commands. Install, launch from the Start menu, and confirm:
catalog refresh, audio playback, Grid thumbnails, the video player (always-on-top,
F11/Escape fullscreen), and language switching - including one right-to-left
language, where the whole layout mirrors.

## Phase 4 - Listing materials (all present in this repo)

| Item | Source |
| --- | --- |
| Copy deck, one file per listing language | `msix/listing/<listing-code>.txt` - see `msix/listing/README.md` |
| Shared rows, search terms, forbidden terms | `msix/listing/shared.txt`, `search-terms.txt`, `forbidden-terms.txt` |
| Import-ready listing CSV (all thirteen columns) | built by `tools/store/build-store-listing-csv.ps1` - see "Listing import via CSV" below |
| Real in-app screenshots, one per language | `assets/store/app-<listing-code>.png` - regenerate with `tools/store/capture-store-screenshots.ps1` |
| Screenshots (composed, 2732×1536) | `assets/store/screenshot-{en,ru}-2732x1536.png` - regenerate with `tools/store/make-store-images.ps1` |
| Real in-app screenshots (recommended to add before submit) | `tools/store/capture-app.ps1 -Name <shot>` |
| Banner / social preview | `assets/store/banner-1280x360.png`, `assets/store/social-preview-1280x640.png` |
| Privacy policy | `docs/privacy.html` → `https://serzhyale.github.io/StreamsPlayer/privacy.html` |
| Category | Primary **Entertainment**, secondary **Music** |
| Price | Free (Retail price dropdown) |

**Screenshots - do this before submitting:** the composed cards satisfy the
minimum, but a media player is far stronger with genuine captures. Launch the app,
refresh the catalog, and run `tools/store/capture-app.ps1` for: (1) catalog List
mode, (2) Grid mode with thumbnails, (3) the video player with controls, (4)
Settings. Upload the real shots; keep 1-2 composed cards only if you want a titled
lead image.

### Listing import via CSV

The copy lives in `msix/listing/` as one plain-text deck per listing language. The CSV is **built**,
never hand-edited: Partner Center requires the `Field`, `ID` and `Type` columns to match the file it
generated for the current submission, and the `ID` numbers are account-specific and undocumented, so
the export is the column contract.

1. **Add every language to the submission first.** Store listings → *Manage additional languages*.
   An import cannot create a column. A language that is not already there has its copy dropped
   **silently** - no error, no warning, nothing in the report.
2. **Re-take the export every time.** App overview → **Store listings → Export listing**. The export
   carries the current submission's asset URLs and defines which columns the next import will accept;
   yesterday's export is not safe to reuse.
3. Build the import file:
   ```powershell
   pwsh -NoProfile -File tools/store/build-store-listing-csv.ps1 `
     -Export tmp/exported-listing.csv
   ```
   Add `-ReplaceCopy` when a claim has changed - by default the builder fills only empty cells, so a
   listing that already reads "English and Russian interface" would keep saying it. Add
   `-ImportFolder msix/dist/store-listing-import` to stage the CSV beside the screenshots instead.
4. **Import listings → Upload .csv** (or **Upload folder** for the staged variant).

Recorded Partner Center behaviour - all of it learned the hard way, and all of it now enforced by the
builder rather than remembered:

- **The import file must be UTF-8 without BOM.** A BOM is rejected. A Partner Center *export*
  arrives with one; the builder strips it and says so. (An earlier version of this document told you
  to keep the BOM. That was wrong.)
- Every field is quoted, records are separated by CRLF, and there is **no** trailing newline.
- The import is all-or-nothing **per language, not per file**. One bad cell discards that language's
  whole column and leaves the others alone - it does not reject the upload. Measured 2026-07-27: a
  single invalid `DesktopScreenshot1` value dropped ten languages while `en-us`, `ru` and `ar`
  imported cleanly. (An earlier version of this document said one bad cell rejects the whole file.
  That was wrong, and it matters: a partial import **changes the submission**, so the export you were
  holding is stale and must be re-taken before the next attempt.)
- A relative image path is rejected in a flat CSV upload - confirmed 2026-07-27, `The value you
  provided is not valid (app-uk.png)`, which is why the flat output carries no image path at all.
  The only value the field accepts is the asset URL of an image **already uploaded to the current
  submission**, so the import can reference screenshots but never create them: upload a new
  language's screenshot in the UI first, then re-export to pick up its asset URL. Whether *Upload
  folder* would accept a relative path is **untested** - the one attempt used the flat upload. Do not
  restate it as fact until someone has actually run it.
- A listing is Incomplete until it has **both** a description and at least one screenshot. A
  text-only language just sits there; Partner Center reports nothing. The builder prints a
  per-language completeness table for exactly this reason.
- **Never copy `OverrideLogosForWin10 = True` into a language with no `StoreLogo` rows of its own.**
  It holds the listing Incomplete with nothing shown on the page - it stranded ten listings once. The
  builder forces it to `False` for any language without its own logos.
- `Title` and `CopyrightTrademarkInformation` are identifiers, not prose: they come from
  `msix/listing/shared.txt` and are written identically into every column.
- Search terms are **written per listing language** in `msix/listing/search-terms.<code>.txt`, falling
  back to the English `search-terms.txt` for any language without one. At most seven per language, no
  duplicates within a set, and nothing matching `msix/listing/forbidden-terms.txt` - the builder exits
  non-zero rather than warning, and checks every set, not just one.
  This reverses the original SP-0034 decision to keep one English set in all thirteen columns. That
  decision bought reviewability and cost discoverability: Store search matches terms literally, and
  nobody types "internet radio" into a Hindi or Arabic query box. The reversal reinstates the risk it
  was avoiding - thirteen sets, of which the owner reads two - so the forbidden list now carries the
  non-Latin transliterations of `iptv` and the per-market competitor names, and the guard is the build
  rather than a reader.
- `-TermsOnly` writes the SearchTerm rows and nothing else. Use it when the submission's copy is
  already right and only the terms are wrong: the import is all-or-nothing per language, so every
  field you re-send is another chance to have a language rejected.
- `ReleaseNotes` is left alone by the builder; fill it per submission.
- The Russian and Ukrainian bodies call the app "Трансляции" / "Трансляції". The Store *title* is
  "Streams Player" in every language regardless.

`msix/store-listing-export.sample.csv` is a committed fixture: a real 453-row export with the copy
cells emptied and all thirteen language columns present. It exists so the round trip is checkable
without a Partner Center session:

```powershell
pwsh -NoProfile -File tools/store/build-store-listing-csv.ps1 -FillNothing
```

Given nothing to fill, the output must be **byte-identical** to the input. That is the guarantee that
the builder cannot corrupt an export it does not understand. `.gitattributes` marks the fixture
`-text` so git never normalizes its line endings.

## Phase 5 - Age rating (IARC)

**Status: complete and live.** The IARC questionnaire was answered **fresh** for
this app, producing Global Rating ID `7fd02683-3ef9-8606-8fb0-3f68aa55f8fc`
(rating date 2026-07-23, Microsoft storefront).

The SZA portable rating ID used by FastMediaSorter does **not** transfer here:
StreamsPlayer can open arbitrary third-party live audio/video URLs, which changes
the questionnaire answers (uncurated online content). It was answered honestly: no
accounts, no purchases, no ads, no user-to-user content publishing; the app can
display uncontrolled third-party streams. Keep this rationale for any future
re-rating - do not reuse a rating ID from another app.

## Phase 6 - Content-policy note (StreamsPlayer-specific)

Apps that open third-party streams draw extra review under the Store's
infringing-content policy. Pre-empt it:

- Frame the app as an **internet-radio / live-stream catalog player** (it is), not a
  piracy tool. The listing already leads with the curated catalog.
- The search term `IPTV player` was the most likely trigger. It is **gone**: it now sits in
  `msix/listing/forbidden-terms.txt`, and the builder fails the run rather than warning if anyone
  puts it back. Do not re-add it to please a keyword tool.
- Paste the runFullTrust justification from `msix/store-listing.md` verbatim (it
  explains the LibVLC media components, the explicit-refresh network model, and the
  absence of accounts/ads/telemetry, with the source link).

## Phase 7 - Submit / update in Partner Center

1. Partner Center → **Apps and games → Streams Player** (Store ID `9NBTD5SXB8TB`).
2. **Packages** → upload `msix/dist/StreamsPlayer-<version>-windows-x64.msix`.
3. **Store listings** → add every language in *Manage additional languages* **first**, then export,
   build and import as described in "Listing import via CSV". Upload screenshots.
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
