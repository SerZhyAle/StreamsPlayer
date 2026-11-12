#requires -Version 7.0
<#
.SYNOPSIS
  SP-0261: read the product's Store identity from msix/identity.json - one machine-readable record, dot-sourced
  by msix/build-msix.ps1 and msix/Test-MsixIdentity.ps1.

.DESCRIPTION
  Read-MsixIdentity returns the identity as an object, or throws and builds nothing. It never falls back to a
  default: an absent file, an unreadable file or a missing value is an error, so a run that cannot find the
  identity stops before it builds (the Store package once carried a wrong value no one had pinned).

  Required: IdentityName, Publisher, PublisherDisplayName, StoreId, PackageFamilyName.
  Optional: IarcRatingId (the rating is filed per content profile by a person; the file records it, never invents it).
  The file holds no account identifier (no Seller ID, no Tenant ID, no client ID) and no credential.

  msix/Assert-MsixPackage.ps1 does NOT read this file: its reserved values are constants of its own, so the
  read-back of the packed archive cannot agree with the build by construction. msix/Test-MsixIdentity.ps1 compares
  the two.
#>

function Read-MsixIdentity {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The Store identity file was not found: $Path. The package is not built without it (msix/README.md)."
    }
    try { $identity = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "The Store identity file $Path is not valid JSON: $($_.Exception.Message)" }
    if ($identity -isnot [pscustomobject]) { throw "The Store identity file $Path does not hold a JSON object." }

    foreach ($key in 'IdentityName', 'Publisher', 'PublisherDisplayName', 'StoreId', 'PackageFamilyName') {
        $value = $identity.PSObject.Properties[$key]?.Value
        if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) {
            throw "The Store identity file $Path has no value for '$key'."
        }
    }
    return $identity
}
