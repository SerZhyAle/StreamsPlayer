<#
    SP-0259 (PAGE-STYLE 3.10 items 1 to 5, SITE-EXPERIENCE 0.6 rule 13): the release the landing's download buttons
    link. The version, the versioned asset links, their .sha256 sidecars and the release date are build-time facts:
    the generator renders them into the markup from tools/site/release-manifest.json, so a visitor without scripts, a
    crawler and a visitor whose browser is rate-limited by api.github.com all receive the same working links. No
    page fetches the release at run time any more.

    The manifest is this product's own file (only its own pages read it): a version, its date, and the names of the
    two assets the landing links. The release flow writes it with tools/site/Write-ReleaseManifest.ps1 (scripts/
    release.ps1, step 2) in the tree it tags, and the site deploys only after that release run concluded success
    (.github/workflows/pages.yml), so a served page never links an asset that is not yet published.

    The reader is strict for the same reason ReleaseNotes.ps1 is: the file is written once per release and a stale or
    hand-edited value is the usual mistake. It refuses a version that is not a stamp, a date that is not that stamp's
    release day, an asset name that is not the one release.yml publishes for that version, and a version the release
    notes do not record (item 5: the manifest names a published release that the release notes record).

    Dot-source it after ReleaseNotes.ps1:
        . "$PSScriptRoot/ReleaseNotes.ps1"
        . "$PSScriptRoot/ReleaseManifest.ps1"
        $release = Get-SiteReleaseManifest -Root $root -ReleaseNotes $notes
#>

function Get-SiteReleaseAssetName {
    # The asset names .github/workflows/release.yml publishes for a version. The one place the site spells them.
    param([Parameter(Mandatory)] [string] $Version)

    return [pscustomobject]@{
        Setup = "StreamsPlayer-$Version-windows-x64-setup.exe"
        Zip   = "StreamsPlayer-$Version-windows-x64.zip"
    }
}

function ConvertTo-SiteReleaseManifestJson {
    # The manifest text for a version, LF line endings and one final newline. Pure: the writer script saves it, the
    # test compares it.
    param([Parameter(Mandatory)] [string] $Version)

    $date = Get-ReleaseDateText -Date (ConvertTo-ReleaseDate -Version $Version)
    $assets = Get-SiteReleaseAssetName -Version $Version
    $manifest = [ordered]@{
        '$comment' = 'SP-0259 (PAGE-STYLE 3.10): the release the site links. Written by tools/site/Write-ReleaseManifest.ps1 in the release flow, never by hand; read by tools/site/ReleaseManifest.ps1.'
        'version'  = $Version
        'date'     = $date
        'assets'   = [ordered]@{ 'setup' = $assets.Setup; 'zip' = $assets.Zip }
    }
    return (($manifest | ConvertTo-Json -Depth 4) -replace "`r`n", "`n") + "`n"
}

function Get-SiteReleaseManifest {
    # The manifest's release with the addresses the pages link: { Version; Date; SetupUrl; SetupSha256Url; ZipUrl;
    # ZipSha256Url; ReleasesUrl }. Throws, naming the file, on anything it cannot vouch for.
    param(
        [Parameter(Mandatory)] [string] $Root,
        # Get-SiteReleaseNote's result: the versions the release notes record.
        [Parameter(Mandatory)] $ReleaseNotes
    )

    $name = 'tools/site/release-manifest.json'
    $path = Join-Path $Root $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "$name does not exist: write it with tools/site/Write-ReleaseManifest.ps1 -Version <the published release>."
    }
    try {
        $data = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json
    }
    catch {
        throw "${name} is not valid JSON: $($_.Exception.Message)"
    }

    $version = [string] $data.PSObject.Properties['version']?.Value
    if ($version -notmatch '^[0-9]{2}\.[0-9]{4}\.[0-9]{4}$') { throw "${name}: version '$version' is not a version stamp (YY.MMDD.HHmm)." }
    $expectedDate = Get-ReleaseDateText -Date (ConvertTo-ReleaseDate -Version $version)
    $date = [string] $data.PSObject.Properties['date']?.Value
    if ($date -cne $expectedDate) { throw "${name}: date '$date' is not the release day of $version ($expectedDate)." }

    $assets = $data.PSObject.Properties['assets']?.Value
    $expected = Get-SiteReleaseAssetName -Version $version
    foreach ($asset in 'setup', 'zip') {
        $actual = if ($assets) { [string] $assets.PSObject.Properties[$asset]?.Value } else { '' }
        $want = if ($asset -eq 'setup') { $expected.Setup } else { $expected.Zip }
        if ($actual -cne $want) { throw "${name}: assets.$asset '$actual' is not the file release.yml publishes for $version ($want)." }
    }

    if (-not (@($ReleaseNotes | ForEach-Object { $_.Version }) -ccontains $version)) {
        throw "${name}: $version has no release notes in msix/listing/release-notes - the manifest names only a release they record."
    }

    $repository = 'https://github.com/SerZhyAle/StreamsPlayer'
    $download = "$repository/releases/download/v$version"
    return [pscustomobject]@{
        Version        = $version
        Date           = $date
        SetupUrl       = "$download/$($expected.Setup)"
        SetupSha256Url = "$download/$($expected.Setup).sha256"
        ZipUrl         = "$download/$($expected.Zip)"
        ZipSha256Url   = "$download/$($expected.Zip).sha256"
        ReleasesUrl    = "$repository/releases"
    }
}
