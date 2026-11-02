<#
.SYNOPSIS
    Writes LICENSE.txt and THIRD-PARTY-NOTICES.txt into a folder (next to the portable downloads, into the Store package).
    Dot-sourced by build-portable.ps1 and build-msix.ps1.

.DESCRIPTION
    The licence is the LICENSE file at the root of the repository (MIT); the notices live in ByteScape.Core\Core\Legal. The
    program embeds the same files and shows them in About. {Name}, {Year} and {Publisher} in the notices are filled from
    Branding.cs the way Core\Legal\LegalTexts.cs does it.
#>
function Write-LegalFiles([string]$Root, [string]$Dest) {
    # UTF-8 explicitly: the source files have no byte order mark, and Get-Content would read them in the local code page
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $branding = [System.IO.File]::ReadAllText((Join-Path $Root "ByteScape.Core\Branding.cs"), $utf8)
    function Const([string]$name) {
        if ($branding -notmatch ('const string ' + $name + '\s*=\s*"([^"]*)"')) { throw "Branding.$name not found" }
        return $Matches[1]
    }
    $name = Const "Name"; $year = Const "CopyrightYear"; $publisher = Const "Publisher"
    if ($publisher -eq "") { $publisher = "the author of $name" }
    $sources = [ordered]@{
        "LICENSE.txt"             = Join-Path $Root "LICENSE"
        "THIRD-PARTY-NOTICES.txt" = Join-Path $Root "ByteScape.Core\Core\Legal\THIRD-PARTY-NOTICES.txt"
    }
    foreach ($file in $sources.Keys) {
        $text = [System.IO.File]::ReadAllText($sources[$file], $utf8)
        $text = $text.Replace("{Name}", $name).Replace("{Year}", $year).Replace("{Publisher}", $publisher)
        # Windows line ends: the files are read in Notepad next to the exe
        $text = $text.Replace("`r`n", "`n").Replace("`n", "`r`n")
        [System.IO.File]::WriteAllText((Join-Path $Dest $file), $text, $utf8)
    }
}
