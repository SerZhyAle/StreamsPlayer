# Microsoft Store package

`build-msix.ps1` publishes the WPF app self-contained for x64, copies the
required StreamsPlayer tile logos from `assets/msix/` and packs a full-trust MSIX. Generated `stage/`
and `dist/` folders are intentionally ignored by Git.

It requires the Windows SDK tools `makeappx.exe` and (for `-SelfSign`)
`signtool.exe`. Install them with `winget install Microsoft.WindowsSDK`.

## Reserved Partner Center identity (permanent)

The product is reserved in Partner Center. These values are permanent for this
product and must be supplied on every update; `build-msix.ps1` already defaults
to the three identity values, so a plain `./msix/build-msix.ps1` produces a
correctly-identified Store package.

| Field | Value |
| --- | --- |
| `Package/Identity/Name` | `SZA.StreamsPlayer` |
| `Package/Identity/Publisher` | `CN=F98ACEDB-1E22-4C39-AF63-F9FCFE807DCD` |
| `Package/Properties/PublisherDisplayName` | `SZA` |
| Package Family Name (PFN) | `SZA.StreamsPlayer_fdk7e19xt9z9j` |
| Package SID | `S-1-15-2-4100386097-3268198982-3756533809-2958099874-1231345077-408212136-1048749765` |
| Store ID | `9NBTD5SXB8TB` |
| Store deep link | `ms-windows-store://pdp/?ProductId=9NBTD5SXB8TB` |
| Web Store URL (after live) | `https://apps.microsoft.com/detail/9NBTD5SXB8TB` |

To override the identity for a one-off, pass the parameters explicitly:

```powershell
.\msix\build-msix.ps1 `
  -IdentityName 'SZA.StreamsPlayer' `
  -Publisher 'CN=F98ACEDB-1E22-4C39-AF63-F9FCFE807DCD' `
  -PublisherDisplayName 'SZA'
```

The version always comes from the `vYY.MMDD.HHmm` tag at HEAD: the script
refuses a dirty working tree (it would pack edits no commit carries) and
refuses a HEAD that is not exactly at the version tag, so a Store package
always carries a version a tag publishes. Build it from a clean checkout at
the tag - `git worktree add <dir> v<version>` when the main tree is busy - or
follow scripts/release.ps1, which tags before the package is built. MSIX
requires a fourth numeric component and **forbids leading zeros** in any part,
so the script maps the tag's version by dropping the zero-padding:
`26.0723.0959` becomes package identity `26.723.959.0`. This stays monotonic
and unique per minute. The three-part `YY.MMDD.HHmm` value is embedded in the
application and shown in Settings.

The Store package must remain unsigned because Microsoft signs it during
certification. For local sideload testing only, use
`./msix/build-msix.ps1 -SelfSign`, trust the generated certificate, then install
the generated package. Never upload a self-signed test package to Partner Center.
