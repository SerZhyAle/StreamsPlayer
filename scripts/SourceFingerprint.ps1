<#
    SP-0239 P5: the one definition of a source fingerprint, for translation freshness (DOC-EXTERNAL-QUALITY rule 3 leaves
    the form to the product and demands only that a stale copy is flagged).

    Get-SourceFingerprint returns `sha256:<first 16 hex digits>` of a file's text: decoded as UTF-8 with a leading BOM
    dropped, and every CRLF read as LF, so the line endings a checkout produces (.gitattributes) never change it.

    Dot-sourced by scripts/check-docs.ps1 (the portal mirrors) and tools/help/build-help.ps1 -Check (the help
    translations); neither keeps a copy of this function.
#>
function Get-SourceFingerprint([string] $Path) {
    $text = [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $digest = [System.Security.Cryptography.SHA256]::HashData([System.Text.UTF8Encoding]::new($false).GetBytes($text))
    return 'sha256:' + [System.Convert]::ToHexString($digest).Substring(0, 16).ToLowerInvariant()
}
