---
paths:
  - "Directory.Build.props"
  - "scripts/*elease*.ps1"
  - "scripts/*Store*.ps1"
  - ".github/workflows/release.yml"
  - ".github/workflows/pages.yml"
  - "msix/**"
  - "winget/**"
  - "manifests/**"
  - "installer/**"
  - "release-verdicts/**"
  - "tools/store/**"
  - "STORE_PUBLISHING.md"
---

# Versioning and release

Detail moved verbatim from `CLAUDE.md` and `AGENTS.md` on 2026-11-25; the always-loaded core is `AGENTS.md`.

## Version convention

- StreamsPlayer versions use the **author's local release time** (Europe/Malta) in `YY.MMDD.HHmm` form,
  for example `26.0719.0131`. The version's purpose is to tell the owner when a build was made, read
  against the clock on his wall; a stamp he has to convert does not serve that purpose. Versions up to
  and including `26.0806.2131` were stamped in UTC, so they read two hours (one in winter) earlier than
  the local time they were built at. The **shape** `YY.MMDD.HHmm` is frozen for the product's life and
  does not change with this.
- Accepted consequence: the autumn clock change repeats one local hour, so a release inside that hour
  could carry a version lower than one already published. Deliberate - releases are not cut on the hour
  boundary of a DST change, and the monotonicity check before a release is the real guard.
- Git tags use the same value with a `v` prefix: `v26.0719.0131`.
- **For a real release the version is not hand-edited.** `.github/workflows/release.yml` derives it from the `v*` tag (regex + a real `ParseExact`, so an impossible stamp fails the job) and passes it as `-p:Version=/-p:AssemblyVersion=/-p:FileVersion=/-p:InformationalVersion=`. The four fields in `Directory.Build.props` are the *local build* stamp only; the tag wins for anything published. The Settings window displays `InformationalVersion`.
- MSIX package identity requires four components and its version schema forbids leading zeros, so `msix/build-msix.ps1` int-casts each component and appends `.0`: `26.0719.0131` → `26.719.131.0` (with a per-component `≤ 65535` ceiling guard). Winget and GitHub retain the canonical zero-padded three-component value.
- A release version must be later than every published version. Do not reuse a timestamp for different package contents.

## Version stamping

- Versioning: `YY.MMDD.HHmm` in the author's **local** time (Europe/Malta), set in [Directory.Build.props](../../Directory.Build.props) - the stamp exists so the owner can see when a build was made without converting time zones. Anything at or below `26.0806.2131` was stamped in UTC and reads 1-2 h early. `build.ps1` (and so `run.ps1`) stamps every local build with its own local clock on the command line (`PACKAGE-VERSIONING` rules 1 and 6) and never writes the props file, so the value there is only the release sentinel and a local build's version is not a release version. A release version must exceed every published version; never reuse a timestamp. For a real release the value comes from the `v*` tag via `release.yml`, not from the props file.

## Contract catch-up

- **Contract catch-up, the first step of every release** (canon `CONTRACTS.md` section 6; SP-0278): before the
  version is cut, generate the spec with the canon's `contract-lag.ps1 -Product StreamsPlayer -EmitSpec`, read each
  log entry since the product's synchronized version against the tree, and move the product's own registry rows and
  exceptions; the last record is [the 2026-11-15 audit](../../docs/audits/SP-0278-contract-catchup.md). A release is not
  cut on a row that is stale or behind without a dated reason.

## Overlay and coupling shape

- **Overlay shape:** Windows-desktop, *installer variant* (SP-0092, changed 2026-08-21 from the no-installer variant) - an Inno Setup per-user setup EXE and a portable ZIP in the same GitHub Release, plus winget-portable and Store MSIX. The installer and the archive are compiled from one publish, so their payloads never differ. winget deliberately stays on the ZIP (`InstallerType: zip` + `NestedInstallerType: portable`); moving it to `inno` is a separate submission. Frozen anchors: winget `PackageIdentifier`, the MSIX Identity `Name`/`Publisher`, and the Inno `AppId` `{15F4F08C-E78B-41B7-9039-6A3332D7D080}` - changing the last one does not upgrade an existing install, it creates a second parallel one on every machine that already has it.
- **Coupling shape:** *consumed published release artifact* - StreamsPlayer depends at runtime on another SZA product's release output (the FastMediaSorter catalog ZIP at `StreamCatalogService.CatalogUrl`). This is neither a wire/config contract nor an edition/parity relationship; the merge protects user-owned `MANUAL`/`IMPORTED` rows and refresh is explicit-only.
