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

**Release notes must not contain `": "`.** Every `REPLACE_...` token sits in a plain YAML scalar, so
a colon-plus-space anywhere in the substituted text makes the YAML scanner read it as a nested
mapping and `winget validate` fails with "mapping values are not allowed in this context". Use a
spaced hyphen instead - the `Description` fields already do, which is why they survive the same
substitution. This is the failure mode to check first when validation rejects a manifest that looks
fine (2026-07-27).

Either way, winget can only be refreshed **after** an approved GitHub Release
exists. The package is live; use the flow above to submit each subsequent version.

Submission state as of 2026-07-28. `winget-pkgs` serves **26.0723.1040**. Two
pull requests are open and unmerged - [#408215](https://github.com/microsoft/winget-pkgs/pull/408215)
for 26.0727.0253 (opened 2026-07-27, still only bot activity) and
[#408825](https://github.com/microsoft/winget-pkgs/pull/408825) for 26.0728.1352.
Review latency there is days, not hours: do not treat an unmerged PR as a failure,
and do not re-submit the same version because it has not landed yet.

The package is published under the identifier `SerZhyAle.StreamsPlayer`. Keep this
permanent identifier for every future submission.

The templates follow manifest schema 1.12.0. Do not bump the schema merely because a newer client exists; use the version currently recommended by the `winget-pkgs` pull-request template. Test the filled installer in Windows Sandbox before submission.

Maintainer references:

- Manifest authoring: `https://learn.microsoft.com/en-us/windows/package-manager/package/manifest`
- Validation: `https://learn.microsoft.com/en-us/windows/package-manager/winget/validate`
- Repository submission: `https://learn.microsoft.com/en-us/windows/package-manager/package/repository`
