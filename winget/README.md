# winget publication

This folder holds source-controlled templates, not submitted manifests. Do not
submit a template: every release needs its own immutable version, GitHub Release
URL and SHA256 hash.

After an explicit release:

1. Download the `StreamsPlayer-<version>-windows-x64.zip` and `.sha256` from the
   GitHub Release created by `release.yml`.
2. Confirm the release/tag uses `YY.MMDD.HHmm`, then copy the four files from `templates/` into a matching `winget-pkgs` manifest
   folder: `manifests/s/SerZhyAle/StreamsPlayer/<version>/`.
3. Replace all `REPLACE_...` values, including both locale release notes, the ZIP SHA256 and ISO `YYYY-MM-DD` release date. All `PackageVersion` values must exactly match the three-part release version.
4. Validate with `winget validate --manifest <folder>` and submit a pull request
   to `microsoft/winget-pkgs`.

The planned identifier is `SerZhyAle.StreamsPlayer`. Confirm its availability
before the first submission; choose a different permanent identifier if winget
requires one.

The templates follow manifest schema 1.12.0. Do not bump the schema merely because a newer client exists; use the version currently recommended by the `winget-pkgs` pull-request template. Test the filled installer in Windows Sandbox before submission.

Maintainer references:

- Manifest authoring: `https://learn.microsoft.com/en-us/windows/package-manager/package/manifest`
- Validation: `https://learn.microsoft.com/en-us/windows/package-manager/winget/validate`
- Repository submission: `https://learn.microsoft.com/en-us/windows/package-manager/package/repository`
