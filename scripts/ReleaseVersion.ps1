<#
    SP-0247 (PACKAGE-VERSIONING P3, W2): the one place the release version is derived from the tag and remapped
    for MSIX. release.yml, msix/build-msix.ps1 and winget/New-WingetManifests.ps1 dot-source this file, and
    scripts/Test-ReleaseVersion.ps1 holds both functions to fixed instants (section 4, rung 2a).
#>

# The tag is the registered `dotted` rendering vYY.MMDD.HHmm. The regex fixes the shape, and the real ParseExact
# refuses a stamp that is no calendar instant (month 13, February 30, hour 24), so an impossible tag fails the
# release instead of becoming a version.
function ConvertFrom-ReleaseTag {
    param([string] $Tag)
    if ($Tag -cnotmatch '^v\d{2}\.\d{4}\.\d{4}$') { throw 'Tag must use vYY.MMDD.HHmm.' }
    $version = $Tag.Substring(1)
    [DateTime]::ParseExact($version, 'yy.MMdd.HHmm', [Globalization.CultureInfo]::InvariantCulture) | Out-Null
    return $version
}

# The MSIX Identity Version schema forbids leading zeros in any part (26.0723.0957.0 is rejected), so each
# component is int-cast and a revision of 0 appended: 26.0723.0957 -> 26.723.957.0. MMDD and HHmm read as
# integers keep their order, so the remap stays monotonic and unique per minute.
function ConvertTo-MsixVersion {
    param([string] $Version)
    $msixVersion = (($Version.Split('.') | ForEach-Object { [int]$_ }) -join '.') + '.0'
    foreach ($part in $msixVersion.Split('.')) { if ([int]$part -gt 65535) { throw "Version part '$part' exceeds 65535." } }
    return $msixVersion
}
