# winget publication

This folder holds source-controlled templates, not submitted manifests. Do not
submit a template: every release needs its own immutable version, GitHub Release
URL and SHA256 hash.

The locale set is deliberately **three** - `en-US`, `ru-RU`, `uk-UA` - even though the application
ships thirteen interface languages. A winget locale is prose the owner has to maintain and re-check
on every release; machine-translated package metadata in ten more languages would be a maintenance
cost with no reader. Do not "complete" this set to match the application.

After an explicit release, the preferred path (matches the sibling SZA apps) is
`wingetcreate`, which recomputes the SHA256 and opens the PR for you:

```powershell
wingetcreate update SerZhyAle.StreamsPlayer `
  --version <YY.MMDD.HHmm> `
  --urls https://github.com/SerZhyAle/StreamsPlayer/releases/download/v<version>/StreamsPlayer-<version>-windows-x64.zip `
  --submit
```

Manual alternative (when you need full control of the manifest):

1. Download the `StreamsPlayer-<version>-windows-x64.zip` and `.sha256` from the
   GitHub Release created by `release.yml`.
2. Confirm the release/tag uses `YY.MMDD.HHmm`, then copy the five files from `templates/` into a matching `winget-pkgs` manifest
   folder: `manifests/s/SerZhyAle/StreamsPlayer/<version>/`.
3. Replace all `REPLACE_...` values, including all three locale release notes (`REPLACE_RELEASE_NOTES`, `REPLACE_RELEASE_NOTES_RU`, `REPLACE_RELEASE_NOTES_UK`), the ZIP SHA256 and ISO `YYYY-MM-DD` release date. All `PackageVersion` values must exactly match the three-part release version.
4. Validate with `winget validate --manifest <folder>` and submit a pull request
   to `microsoft/winget-pkgs`.

**Read this before writing a single word of the pull request.** Fetch
`microsoft/winget-pkgs` `.github/PULL_REQUEST_TEMPLATE.md` at submission time and use it verbatim -
its headings, its wording, its checklist, in its order - putting an `x` only in boxes that are
genuinely true. Never write a body of your own structure, never paraphrase the checklist from memory,
and never append this project's own commit footer: the maintainer bots parse the template, so a
rewritten one reads as an unfilled one. The template also states the **title** format in an HTML
comment on its first line. For a package already in the repository that is
`Update: SerZhyAle.StreamsPlayer to <version>`, **not** `New version: ...`.

**Correction, checked against the API on 2026-08-21: the `New-Manifest` label is not the tell this
note claimed it was.** It said a wrong title earns that label and a right one avoids it. It does not.
Every merged submission this package has - #414229, #420274, #421532 - went out with the correct
`Update:` title and carries `New-Manifest` anyway, beside `Validation-Completed`,
`Moderator-Approved` and `Publish-Pipeline-Succeeded`. The bot applies it because the pull request
*adds* a manifest folder, which every new version does. #422124 has it too. **Do not chase it, do not
re-open a submission over it, and do not read it as evidence the title was wrong** - check the title
against the template's own HTML comment instead, which is the only thing that actually settles it.
Use the right title because it is the right title, not to dodge a label.

The title itself did go wrong once, on #414229 on 2026-08-09 - opened with a hand-written body and a
`New version:` title. That is the reason the "use the template verbatim" rule above exists; the label
was never the symptom. The template changes: on 2026-08-09 it carried
`## 📖 Description`, `## ✅ Checklist` and `## 📦 Manifest Checklist`, including a
`This PR only modifies one (1) manifest` box that did not exist in July. Fetch it; do not reproduce
the version quoted here.

**Release notes must not contain `": "`.** Every `REPLACE_...` token sits in a plain YAML scalar, so
a colon-plus-space anywhere in the substituted text makes the YAML scanner read it as a nested
mapping and `winget validate` fails with "mapping values are not allowed in this context". Use a
spaced hyphen instead - the `Description` fields already do, which is why they survive the same
substitution. This is the failure mode to check first when validation rejects a manifest that looks
fine (2026-07-27).

Either way, winget can only be refreshed **after** an approved GitHub Release
exists. The package is live; use the flow above to submit each subsequent version.

Submission state re-checked against the API on 2026-08-09. `winget show
SerZhyAle.StreamsPlayer --versions` still lists only four versions, up to
**26.0728.1352**: [#413363](https://github.com/microsoft/winget-pkgs/pull/413363)
for 26.0806.2131 has been **open since 2026-08-06** and has not merged. Review
latency there is days, not hours - do not treat an unmerged PR as a failure, and
do not re-submit the same version because it has not landed yet.

26.0809.0022 was submitted on 2026-08-09 as
[#414229](https://github.com/microsoft/winget-pkgs/pull/414229), and #413363 was
then **closed as superseded** on the owner's call: 26.0809.0022 contains
everything 26.0806.2131 did, and its archive is about 36% smaller because the
LibVLC native-tree fix landed after that version was cut. Merging both would have
added a strictly worse 221 MB build to the version list. Two open submissions for
the same package would have been legal - winget carries versions, not a chain -
so this was hygiene, not necessity. The earlier two,
[#408215](https://github.com/microsoft/winget-pkgs/pull/408215) for 26.0727.0253
and [#408825](https://github.com/microsoft/winget-pkgs/pull/408825) for
26.0728.1352, merged normally. 26.0730.1512 was never submitted here at all.

Re-checked against the API on 2026-08-19 before submitting: the winget-pkgs tree
carries five versions, up to and including **26.0809.0022** (#414229 merged
2026-08-09), and no StreamsPlayer pull request was open. 26.0819.0156 was then
submitted as
[#420274](https://github.com/microsoft/winget-pkgs/pull/420274).

Re-checked against the API on 2026-08-20 before submitting: #420274 **merged**
the same day it was opened (2026-08-19), the tree now carries six versions up to
**26.0819.0156**, and no StreamsPlayer pull request was open. 26.0820.1828 was
then submitted as
[#421532](https://github.com/microsoft/winget-pkgs/pull/421532), through the
fork-branch plus contents-API path described above; `wingetcreate` was not
attempted, for the reason recorded below it.

Re-checked against the API on 2026-08-21 before submitting: #421532 merged, the tree carries seven
versions up to and including **26.0820.1828**, and no StreamsPlayer pull request was open.
26.0821.1208 was then submitted as
[#422124](https://github.com/microsoft/winget-pkgs/pull/422124), same fork-branch plus contents-API
path, five files, additions only, and the title got no `New-Manifest` label.

**That submission mattered more than a version bump.** 26.0820.1828 - the version this repository was
serving - carries a WPF runtime that breaks `MediaElement` network audio outright, so `winget install`
was handing people an application that opens its catalog and plays nothing at all. The lesson is not
about winget: **a channel that was not touched is not a channel that is fine.** What it delivers can
break underneath a manifest nobody edited, and the only way to know is to install from the channel and
use the product, which is exactly what the local install test below now buys.

**The unticked box is now tickable, and was ticked.** `LocalManifestFiles` turned out to be already
enabled on the machine, so the full ladder ran for the first time in this package's history:
`winget install --manifest` downloaded the asset, verified the hash itself, extracted the portable
payload and registered the `streamsplayer` alias; the installed payload was then launched against a
live station and reached `AUDIO LIVE`; `winget uninstall` removed it and left `%LOCALAPPDATA%\StreamsPlayer`
intact. Check `winget settings export | ConvertFrom-Json` for `adminSettings.LocalManifestFiles`
before assuming the elevation problem below still applies to this machine.

**One checklist box went out unticked, deliberately.** `winget install --manifest`
needs `winget settings --enable LocalManifestFiles`, which needs an elevated
shell. Where the release runs without one, the box is a lie and must stay empty -
the pull-request body says so and names what was verified instead: the asset
downloaded from the manifest's own `InstallerUrl`, its SHA256 recomputed and
compared to `InstallerSha256`, and the declared `NestedInstallerFiles` entry
confirmed present in the archive. Enable the setting **before** starting a
submission if the full ladder is wanted; opening the pull request first and
pushing the missing check afterwards only cancels the in-flight validation run.

**Encoding, learned from reading a merged manifest rather than from prose**: the
five files in winget-pkgs are UTF-8 **with a BOM** and **LF** endings. The
templates in this folder carry no BOM, so a set generated by copying them lands
without one. Add `EF BB BF` to each file before submitting, and keep the endings
LF - the working tree here is LF, and Git's autocrlf would otherwise put CRLF in
the file the API uploads. `winget validate` passes either way, so this is not
something the gate will catch for you.

**The lesson worth keeping**: this note claimed 26.0806.2131 was submitted
cleanly and left it at that, so the next release assumed the source carried it.
It did not - the PR sat open for three days. Re-check `winget show <id>
--versions` against the API at the start of a release, not the note.

`wingetcreate ... --submit` could not open that pull request: it refuses to run
until the fork's `master` is synced with upstream, and the sync fails because the
`gh` OAuth token carries `repo` but not `workflow`, while upstream has changed
files under `.github/workflows/`. The path that works without widening the token
is to branch the fork at **its own** `master`, add the five manifest files through
the contents API, and open the pull request from that branch: GitHub diffs against
the merge base, so a fork thousands of commits behind still produces a
five-file, additions-only pull request. Do not "fix" this by granting `workflow`
scope to a token that only needs to add manifests.

The package is published under the identifier `SerZhyAle.StreamsPlayer`. Keep this
permanent identifier for every future submission.

The templates follow manifest schema 1.12.0. Do not bump the schema merely because a newer client exists; use the version currently recommended by the `winget-pkgs` pull-request template. Test the filled installer in Windows Sandbox before submission.

Maintainer references:

- Manifest authoring: `https://learn.microsoft.com/en-us/windows/package-manager/package/manifest`
- Validation: `https://learn.microsoft.com/en-us/windows/package-manager/winget/validate`
- Repository submission: `https://learn.microsoft.com/en-us/windows/package-manager/package/repository`
