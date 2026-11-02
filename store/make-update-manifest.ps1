<#
.SYNOPSIS
    Writes the update description the portable exe reads (Branding.UpdateUrl points at where it is published).

.DESCRIPTION
    {"version", "url", "notes", "published", "sha256"}: the version is read from the exe unless given, the SHA-256 of the
    exe lets users check the file they download (the update window shows it). Publish the file at the address in
    Branding.UpdateUrl (https only), next to or after the new exe's download page.

.EXAMPLE
    .\store\make-update-manifest.ps1 -Url "https://example.com/bytescape/download" -Notes "Faster scans. Seven languages."
#>
param(
    [Parameter(Mandatory = $true)][string]$Url,
    [string]$Exe = "",
    [string]$Version = "",
    [string]$Notes = "",
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"
# defaults next to this script (Windows PowerShell leaves $PSScriptRoot empty inside param())
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($Exe -eq "") {
    # the standalone download store\build-portable.ps1 made, else the published exe
    $built = Get-ChildItem (Join-Path $here "out\portable") -Filter "ByteScape-*-x64.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $Exe = if ($built) { $built.FullName } else { Join-Path $here "..\publish\ByteScape.exe" }
}
if ($Out -eq "") { $Out = Join-Path $here "out\update.json" }

if (-not $Url.StartsWith("https://")) { throw "The download address must start with https:// (the program opens nothing else)." }
$exePath = (Resolve-Path $Exe).Path
if ($Version -eq "") {
    $v = (Get-Item $exePath).VersionInfo
    $Version = "{0}.{1}.{2}" -f $v.ProductMajorPart, $v.ProductMinorPart, $v.ProductBuildPart
}
if ($Version -notmatch '^\d+(\.\d+){1,3}$') { throw "Version '$Version' is not like 1.2.3 (pre-releases are never offered)." }
$sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $exePath).Hash

$manifest = [ordered]@{
    version   = $Version
    url       = $Url
    notes     = $Notes
    published = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    sha256    = $sha
}
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
# UTF-8 without a byte order mark: what every web server serves as JSON
[System.IO.File]::WriteAllText($Out, ($manifest | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Written $Out"
Write-Host "  version $Version, SHA-256 $sha"
Write-Host "Publish it at the address in Branding.UpdateUrl."
