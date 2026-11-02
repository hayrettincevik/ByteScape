# Runs the Windows App Certification Kit (WACK) on the Store package, the check Partner Center runs on every submission.
# Needs administrator rights: started normally it builds the dev-signed package as you, then asks once (UAC) and runs the
# elevated half itself. The elevated half trusts the dev certificate on this PC only for the duration of the test
# (Local Machine \ Trusted People) and removes it again afterwards. Report: store\out\wack.xml, summary printed at the end.
#
#   powershell -ExecutionPolicy Bypass -File store\run-wack.ps1
#   powershell -ExecutionPolicy Bypass -File store\run-wack.ps1 -SkipBuild     (test the package built last time)
param([switch]$SkipBuild, [switch]$Elevated)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here "out"
$msix = Join-Path $out "ByteScape_1.0.0.0_x64.msix"
$cer = Join-Path $out "ByteScapeDev.cer"
$report = Join-Path $out "wack.xml"
$log = Join-Path $out "wack-log.txt"
$appcert = "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe"

if (-not $Elevated) {
    if (-not (Test-Path $appcert)) { throw "The App Certification Kit is not installed ($appcert): install it with the Windows SDK." }
    if (-not $SkipBuild) { & (Join-Path $here "build-msix.ps1") -Sign }
    if (-not (Test-Path $msix)) { throw "$msix is missing: run without -SkipBuild." }
    # the dev certificate lives in your own certificate store: hand its public part to the elevated half
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq "CN=ByteScape Dev" } | Select-Object -First 1
    if (-not $cert) { throw "No 'CN=ByteScape Dev' certificate: run without -SkipBuild." }
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Remove-Item $report, $log -ErrorAction SilentlyContinue
    Write-Host "Asking for administrator rights (UAC); the test takes a few minutes..."
    Start-Process powershell -Verb RunAs -Wait -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Elevated")
    if (Test-Path $log) { Get-Content $log }
    if (-not (Test-Path $report)) { Write-Host "No report (the prompt was declined or the test did not run)."; return }
    [xml]$x = Get-Content $report -Encoding UTF8
    $overall = $x.REPORT.OVERALL_RESULT
    Write-Host ""
    Write-Host "OVERALL: $overall   (full report: $report)"
    foreach ($t in $x.SelectNodes("//TEST")) {
        $result = ($t.RESULT.InnerText, $t.RESULT, "?" | Where-Object { $_ } | Select-Object -First 1)
        if ("$result".Trim() -ne "PASS") { Write-Host ("  {0}: {1}" -f $t.NAME, "$result".Trim()) }
    }
    return
}

# ---- elevated half
Start-Transcript -Path $log -Force | Out-Null
$trusted = $null
try {
    $trusted = Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
    & $appcert reset
    & $appcert test -appxpackagepath $msix -reportoutputpath $report
    Write-Host "appcert exit code: $LASTEXITCODE"
}
catch { Write-Host "Failed: $($_.Exception.Message)" }
finally {
    # trust the dev certificate no longer than the test
    if ($trusted) { Remove-Item (Join-Path "Cert:\LocalMachine\TrustedPeople" $trusted.Thumbprint) -ErrorAction SilentlyContinue }
    Stop-Transcript | Out-Null
}
