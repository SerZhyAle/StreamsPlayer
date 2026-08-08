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
`Update: SerZhyAle.StreamsPlayer to <version>`, **not** `New version: ...`; the wrong title gets the
submission labelled `New-Manifest`, which says an existing package is brand new, and editing the
title afterwards does not take the label back off.

This has now gone wrong on three submissions - #408215 on 2026-07-27, and #414229 on 2026-08-09,
which was opened with a hand-written body and the `New version:` title and picked up `New-Manifest`
within the minute. It is written here, at the step where it happens, precisely because it was already
written in `memory/MEMORY.md` and that was not enough. The template changes: on 2026-08-09 it carried
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
