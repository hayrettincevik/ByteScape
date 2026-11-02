# Builds the Microsoft Store package: publish (self-contained x64, no single-file extraction) -> Assets +
# AppxManifest -> resources.pri (scale qualified logos) -> makeappx pack -> optional signing for sideload tests.
#
#   .\build-msix.ps1                                  # dev identity, unsigned  -> store\out\ByteScape_1.0.0.0_x64.msix
#   .\build-msix.ps1 -Sign                            # + self-signed dev certificate for local install tests
#   .\build-msix.ps1 -IdentityName 12345Pub.ByteScape -Publisher "CN=GUID" -PublisherDisplayName "Pub" -Version 1.0.1.0
#                                                     # Store submission package (values from Partner Center)
param(
    [string]$IdentityName = "ByteScapeDev.ByteScape",
    [string]$Publisher = "CN=ByteScape Dev",
    [string]$PublisherDisplayName = "ByteScape Dev",
    [string]$Version = "1.0.0.0",
    [string]$Dotnet = "",
    [switch]$Sign,
    [switch]$SkipPublish
)
$ErrorActionPreference = "Stop"
if ($Dotnet -eq "") {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    $Dotnet = if ($cmd) { $cmd.Source } else { Join-Path $env:USERPROFILE "dotnet\dotnet.exe" }
}
# an SDK in the user folder is found through DOTNET_ROOT (same as build-portable.ps1)
if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = Split-Path -Parent $Dotnet }
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
. (Join-Path $PSScriptRoot "legal-files.ps1")
$stage = Join-Path $PSScriptRoot "build\app"
$out = Join-Path $PSScriptRoot "out"
$kits = "C:\Program Files (x86)\Windows Kits\10\bin"
$sdk = Get-ChildItem $kits -Directory | Where-Object { Test-Path (Join-Path $_.FullName "x64\makeappx.exe") } | Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdk) { throw "Windows SDK with makeappx.exe not found under $kits" }
$makeappx = Join-Path $sdk.FullName "x64\makeappx.exe"
$makepri = Join-Path $sdk.FullName "x64\makepri.exe"
$signtool = Join-Path $sdk.FullName "x64\signtool.exe"
"Windows SDK $($sdk.Name)"

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Version must have four parts (Store requires x.y.z.0)" }

# 1. publish the app into the package folder (framework files in the package, nothing extracted at run time)
if (-not $SkipPublish) {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"
    & $Dotnet publish (Join-Path $root "ByteScape.Wpf\ByteScape.Wpf.csproj") -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=none -p:Version=$($Version.Substring(0, $Version.LastIndexOf('.'))) -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    Get-ChildItem $stage -Filter *.pdb | Remove-Item
}

# 2. assets + manifest (+ the licence terms and third-party notices in the package root)
Copy-Item (Join-Path $PSScriptRoot "Assets") (Join-Path $stage "Assets") -Recurse -Force
Write-LegalFiles -Root $root -Dest $stage
$manifest = Get-Content (Join-Path $PSScriptRoot "AppxManifest.template.xml") -Raw
$manifest = $manifest.Replace("{{IdentityName}}", $IdentityName).Replace("{{Publisher}}", $Publisher).Replace("{{PublisherDisplayName}}", $PublisherDisplayName).Replace("{{Version}}", $Version)
[System.IO.File]::WriteAllText((Join-Path $stage "AppxManifest.xml"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

# 3. resources.pri so that Assets\X.png resolves to the scale-/targetsize- qualified files
$priCfg = Join-Path $PSScriptRoot "build\priconfig.xml"
& $makepri createconfig /cf $priCfg /dq en-US_scale-200 /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed" }
# one package with every scale inside (no resource packages) - simplest for a desktop app
(Get-Content $priCfg) -notmatch "autoResourcePackage" | Set-Content $priCfg -Encoding UTF8
& $makepri new /pr $stage /cf $priCfg /of (Join-Path $stage "resources.pri") /mn (Join-Path $stage "AppxManifest.xml") /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri new failed" }

# 4. pack (never ship a config file written by a test run of the staged app)
Remove-Item (Join-Path $stage "ByteScapeConfig.xml") -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null
$msix = Join-Path $out "ByteScape_${Version}_x64.msix"
if (Test-Path $msix) { Remove-Item $msix }
& $makeappx pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }

# 5. optional dev signing (sideload tests only; the Store signs submissions itself)
if ($Sign) {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName "ByteScape dev signing" `
            -CertStoreLocation Cert:\CurrentUser\My -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
        Export-Certificate -Cert $cert -FilePath (Join-Path $out "ByteScapeDev.cer") | Out-Null
        "dev certificate created; to install the package once, import store\out\ByteScapeDev.cer into 'Local Machine \ Trusted People' (admin)"
    }
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed" }
}

"package: $msix ($([math]::Round((Get-Item $msix).Length / 1MB, 1)) MB)"
