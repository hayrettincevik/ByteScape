# Compares ByteScape's fast NTFS scan (master file table) with the normal folder scan on real drives or folders.
# Needs administrator rights: when started normally it asks for them (UAC) once and runs itself again elevated.
# Read only, except with -MakeData: then the NTFS test data set is (re)created in the first folder given
# (its own files only; the symbolic links in it need administrator rights). The report goes to %TEMP%\bytescape-mftcheck.txt.
#
#   powershell -ExecutionPolicy Bypass -File <this file>                          (C: drive)
#   powershell -ExecutionPolicy Bypass -File <this file> -Path D
#   powershell -ExecutionPolicy Bypass -File <this file> -Path "C:\some\folder"   (folders are also compared with alternate data streams counted)
#   powershell -ExecutionPolicy Bypass -File <this file> -Path "$env:TEMP\bytescape-mftdata;C" -MakeData   (several, separated by ';')
#   powershell -ExecutionPolicy Bypass -File <this file> -Path "$env:TEMP\bytescape-mftdata" -Clean      (only removes the test data)
# The test data made with -MakeData belongs to the administrators; it is removed again right after the check.
param([string]$Path = "C", [switch]$MakeData, [switch]$Clean, [string]$Report = (Join-Path $env:TEMP "bytescape-mftcheck.txt"))
$targets = @($Path -split ';' | Where-Object { $_.Trim() } | ForEach-Object {
    $t = $_.Trim()
    if ($t -match '^[A-Za-z]:?\\?$') { $t.Substring(0, 1) + ":" } else { $t.TrimEnd('\') }
})
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    if (-not $Clean) { Remove-Item $Report -ErrorAction SilentlyContinue }   # a cleanup keeps the last report
    Write-Host "Asking for administrator rights (UAC)..."
    $argv = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Path", "`"$($targets -join ';')`"", "-Report", "`"$Report`"")
    if ($MakeData) { $argv += "-MakeData" }
    if ($Clean) { $argv += "-Clean" }
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList $argv
    if ($Clean) { Write-Host ("Test data removed: " + -not (Test-Path $targets[0])); return }
    if (Test-Path $Report) { Get-Content $Report -Encoding UTF8 } else { Write-Host "No report (the prompt was declined or the check failed)." }
    return
}
# an SDK installed in the user folder is found through DOTNET_ROOT
if (-not $env:DOTNET_ROOT) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    $env:DOTNET_ROOT = if ($cmd) { Split-Path -Parent $cmd.Source } else { Join-Path $env:USERPROFILE "dotnet" }
}
$exe = Join-Path $PSScriptRoot "bin\Release\net8.0-windows\ByteScape.Diag.exe"
if ($Clean) {
    & $exe mftdata $targets[0] clean | Out-Host
    return
}
if ($MakeData) {
    Write-Host "Creating the NTFS test data in $($targets[0]) ..."
    & $exe mftdata $targets[0] | Out-Host
}
$parts = @()
for ($i = 0; $i -lt $targets.Count; $i++) {
    $one = if ($targets.Count -gt 1) { "$Report.$i.txt" } else { $Report }
    Write-Host "Scanning $($targets[$i]) (fast NTFS scan, then the normal scan); this takes about a minute..."
    & $exe mftcheck $targets[$i] $one | Out-Host
    $parts += $one
}
if ($targets.Count -gt 1) {
    ($parts | ForEach-Object { Get-Content $_ -Raw -Encoding UTF8 }) -join "`r`n" | Set-Content $Report -Encoding UTF8
    $parts | Remove-Item -ErrorAction SilentlyContinue
}
if ($MakeData) {
    Write-Host "Removing the NTFS test data..."
    & $exe mftdata $targets[0] clean | Out-Host
}
