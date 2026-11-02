<#
.SYNOPSIS
    Builds the two portable downloads of ByteScape into store\out\portable.

.DESCRIPTION
    ByteScape-<version>-x64.exe        runs on any Windows 10/11 x64 PC: .NET is inside the exe, compressed
                                       (about 67 MB instead of 147 MB; starts about 0.3 s slower and uses about 55 MB more memory)
    ByteScape-<version>-x64-net8.exe   about 2 MB: uses the .NET 8 Desktop Runtime installed on the PC (a newer major version
                                       when 8 is missing); without one, Windows shows where to get it

    Writes README.txt, the licence (LICENSE.txt) and THIRD-PARTY-NOTICES.txt beside them, and SHA256SUMS.txt with the
    SHA-256 of each exe (for the release page, the download page and store\make-update-manifest.ps1).
    The exes carry no debug directory (DebugType=none): no build-machine paths end up in them.
    The user's own publish\ folder is not touched.

.EXAMPLE
    .\store\build-portable.ps1
#>
param(
    [string]$Dotnet = "",
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here "legal-files.ps1")
$root = Resolve-Path (Join-Path $here "..")
if ($Out -eq "") { $Out = Join-Path $here "out\portable" }
if ($Dotnet -eq "") {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    $Dotnet = if ($cmd) { $cmd.Source } else { Join-Path $env:USERPROFILE "dotnet\dotnet.exe" }
}
if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = Split-Path -Parent $Dotnet }
$project = Join-Path $root "ByteScape.Wpf\ByteScape.Wpf.csproj"
$work = Join-Path $here "out\portable-build"
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Out | Out-Null

# the version and the project page as the program carries them (Branding.cs)
$branding = Get-Content (Join-Path $root "ByteScape.Core\Branding.cs") -Raw
if ($branding -notmatch 'Version\s*=\s*"([0-9.]+)"') { throw "Branding.Version not found" }
$version = $Matches[1]
if ($branding -notmatch 'HomeUrl\s*=\s*"([^"]*)"') { throw "Branding.HomeUrl not found" }
$homeUrl = $Matches[1]

Write-Host "Standalone (compressed) ..."
& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o (Join-Path $work "full") | Out-Host
if ($LASTEXITCODE -ne 0) { throw "publish (standalone) failed" }
Write-Host "Small (uses the installed .NET) ..."
& $Dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:RollForward=Major -p:DebugType=none -o (Join-Path $work "net") | Out-Host
if ($LASTEXITCODE -ne 0) { throw "publish (small) failed" }

$full = Join-Path $Out "ByteScape-$version-x64.exe"
$net = Join-Path $Out "ByteScape-$version-x64-net8.exe"
Copy-Item (Join-Path $work "full\ByteScape.exe") $full -Force
Copy-Item (Join-Path $work "net\ByteScape.exe") $net -Force
Remove-Item -Recurse -Force $work

$fullHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash
$netHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $net).Hash
$readme = @"
ByteScape $version - portable, nothing to install

ByteScape-$version-x64.exe
    Runs on any 64-bit Windows 10 or 11 PC. $([math]::Round((Get-Item $full).Length / 1MB)) MB.
    SHA-256: $fullHash

ByteScape-$version-x64-net8.exe
    The same program, $([math]::Round((Get-Item $net).Length / 1MB, 1)) MB: it uses the Microsoft .NET 8 Desktop Runtime
    installed on the PC (or a newer one). Without it, Windows shows where to download it:
    https://dotnet.microsoft.com/download/dotnet/8.0 (".NET Desktop Runtime", x64).
    SHA-256: $netHash

Free and open source (MIT License): no ads, no trial, no account. License: LICENSE.txt.
Components and their licenses: THIRD-PARTY-NOTICES.txt.
Source code, new versions and issues: $homeUrl

Put the exe in a folder of its own: its settings are kept next to it (ByteScapeConfig.xml).
Check a download with PowerShell:  Get-FileHash .\ByteScape-$version-x64.exe
"@
[System.IO.File]::WriteAllText((Join-Path $Out "README.txt"), $readme, (New-Object System.Text.UTF8Encoding($false)))
$sums = "$($fullHash.ToLowerInvariant())  $(Split-Path -Leaf $full)`n$($netHash.ToLowerInvariant())  $(Split-Path -Leaf $net)`n"
[System.IO.File]::WriteAllText((Join-Path $Out "SHA256SUMS.txt"), $sums, (New-Object System.Text.UTF8Encoding($false)))
Write-LegalFiles -Root $root -Dest $Out

Write-Host ""
Write-Host ("{0,-40} {1,8:N1} MB  {2}" -f (Split-Path -Leaf $full), ((Get-Item $full).Length / 1MB), $fullHash)
Write-Host ("{0,-40} {1,8:N1} MB  {2}" -f (Split-Path -Leaf $net), ((Get-Item $net).Length / 1MB), $netHash)
Write-Host "Written to $Out"
