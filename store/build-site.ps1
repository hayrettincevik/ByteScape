<#
.SYNOPSIS
    Puts together the web site of ByteScape in store\out\site: the download page, the privacy policy the Store listing links
    to, the licence files, the pictures and the portable downloads. Any static web host can serve the folder as it is.

.DESCRIPTION
    Needs store\build-portable.ps1 (the downloads) and the Store screenshots (store\out\screenshots\en, made by the
    StoreShotTests test) to have run first. Texts come from site\*.html; {{...}} tokens are filled from Branding.cs and the
    downloads, the privacy page's text from PRIVACY.md. The project page and the update check are on GitHub; a site of its
    own is optional. With -BaseUrl it also writes update.json, for a site that should answer the update check instead of
    the GitHub releases (Branding.UpdateUrl is then "<BaseUrl>/update.json"). With -StoreUrl the page offers the Store app first.

.EXAMPLE
    .\store\build-site.ps1
    .\store\build-site.ps1 -BaseUrl "https://example.github.io/bytescape" -StoreUrl "https://apps.microsoft.com/detail/9XXXXXXXXXXX"
#>
param(
    [string]$BaseUrl = "",
    [string]$StoreUrl = "",
    [string]$Notes = ""
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
. (Join-Path $here "legal-files.ps1")
$utf8 = New-Object System.Text.UTF8Encoding($false)

# UTF-8 explicitly: Branding.cs and PRIVACY.md have no byte order mark
$branding = [System.IO.File]::ReadAllText((Join-Path $root "ByteScape.Core\Branding.cs"), $utf8)
function Const([string]$name) {
    if ($branding -notmatch ('const string ' + $name + '\s*=\s*(/\*loc\*/\s*)?"([^"]*)"')) { throw "Branding.$name not found" }
    return $Matches[2]
}
$version = Const "Version"

$out = Join-Path $here "out\site"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force (Join-Path $out "img"), (Join-Path $out "download") | Out-Null

# pictures: the logo and the English Store screenshots
Copy-Item (Join-Path $here "Assets\Square44x44Logo.targetsize-256.png") (Join-Path $out "img\logo.png")
$shots = Join-Path $here "out\screenshots\en"
foreach ($p in "1-map.png", "2-compare.png", "3-programs.png", "4-forecast.png") {
    $src = Join-Path $shots $p
    if (-not (Test-Path $src)) { throw "$src is missing: run the StoreShotTests test with BYTESCAPE_STORE_SHOTS=$here\out\screenshots first." }
    Copy-Item $src (Join-Path $out "img\$p")
}

# downloads
$portable = Join-Path $here "out\portable"
$full = Join-Path $portable "ByteScape-$version-x64.exe"
$net = Join-Path $portable "ByteScape-$version-x64-net8.exe"
foreach ($f in $full, $net) { if (-not (Test-Path $f)) { throw "$f is missing: run store\build-portable.ps1 first." } }
Copy-Item $full, $net (Join-Path $out "download")
Write-LegalFiles $root $out

# PRIVACY.md is the privacy policy (GitHub shows it as it is); the site gets it as HTML. Only what the file uses is
# understood: headings, paragraphs, "- " lists, **bold**, *italic*, `code`, [links](...) and bare https addresses.
function Inline([string]$t) {
    $t = [System.Net.WebUtility]::HtmlEncode($t)
    $t = [regex]::Replace($t, '`([^`]+)`', '<code>$1</code>')
    $t = [regex]::Replace($t, '\*\*([^*]+)\*\*', '<strong>$1</strong>')
    $t = [regex]::Replace($t, '\*([^*]+)\*', '<em>$1</em>')
    $t = [regex]::Replace($t, '\[([^\]]+)\]\(([^)\s]+)\)', '<a href="$2">$1</a>')
    $t = [regex]::Replace($t, '(?<![">])(https://[^\s<]*[^\s<.,;:)])', '<a href="$1">$1</a>')
    return $t
}
function MarkdownToHtml([string]$md) {
    $html = New-Object System.Text.StringBuilder
    foreach ($block in [regex]::Split($md.Replace("`r`n", "`n").Trim(), '\n\s*\n')) {
        if ($block -match '^(#{1,3}) (.*)$') { $n = $Matches[1].Length; [void]$html.AppendLine("<h$n>$(Inline $Matches[2])</h$n>") }
        elseif ($block.StartsWith("- ")) {
            [void]$html.AppendLine("<ul>")
            foreach ($item in [regex]::Split($block, '\n(?=- )')) { [void]$html.AppendLine("  <li>$(Inline ($item.Substring(2) -replace '\s*\n\s*', ' '))</li>") }
            [void]$html.AppendLine("</ul>")
        }
        else { [void]$html.AppendLine("<p>$(Inline ($block -replace '\s*\n\s*', ' '))</p>") }
    }
    return $html.ToString()
}

function Size([string]$path) {
    $mb = (Get-Item $path).Length / 1MB
    return ([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, "{0:0.0} MB", $mb))
}
$tokens = [ordered]@{
    "{{VERSION}}"   = $version
    "{{YEAR}}"      = Const "CopyrightYear"
    "{{PUBLISHER}}" = Const "Publisher"
    "{{HOME}}"      = Const "HomeUrl"
    "{{PRIVACY}}"   = MarkdownToHtml ([System.IO.File]::ReadAllText((Join-Path $root "PRIVACY.md"), $utf8))
    "{{FULL_NAME}}" = Split-Path -Leaf $full
    "{{FULL_SIZE}}" = Size $full
    "{{FULL_SHA}}"  = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash
    "{{NET_NAME}}"  = Split-Path -Leaf $net
    "{{NET_SIZE}}"  = Size $net
    "{{NET_SHA}}"   = (Get-FileHash -Algorithm SHA256 -LiteralPath $net).Hash
    "{{STORE_URL}}" = $StoreUrl
    # without a Store page the download is the main button
    "{{PORTABLE_CLASS}}" = $(if ($StoreUrl -eq "") { " primary" } else { "" })
}
foreach ($page in Get-ChildItem (Join-Path $root "site") -File) {
    $dest = Join-Path $out $page.Name
    if ($page.Extension -ne ".html") { Copy-Item $page.FullName $dest; continue }
    $text = [System.IO.File]::ReadAllText($page.FullName, $utf8)
    if ($StoreUrl -eq "") { $text = [regex]::Replace($text, '<!--STORE-->.*?<!--/STORE-->', '', 'Singleline') }
    else { $text = $text.Replace("<!--STORE-->", "").Replace("<!--/STORE-->", "") }
    foreach ($k in $tokens.Keys) { $text = $text.Replace($k, $tokens[$k]) }
    if ($text -match '\{\{[A-Z_]+\}\}') { throw "$($page.Name): token $($Matches[0]) is not filled" }
    [System.IO.File]::WriteAllText($dest, $text, $utf8)
}
# GitHub Pages: serve the files as they are
[System.IO.File]::WriteAllText((Join-Path $out ".nojekyll"), "", $utf8)

if ($BaseUrl -ne "") {
    $BaseUrl = $BaseUrl.TrimEnd("/")
    & (Join-Path $here "make-update-manifest.ps1") -Url "$BaseUrl/#download" -Exe $full -Notes $Notes -Out (Join-Path $out "update.json")
    Write-Host "Branding.UpdateUrl = `"$BaseUrl/update.json`"   Branding.HomeUrl = `"$BaseUrl/`""
    Write-Host "Privacy policy URL for Partner Center: $BaseUrl/privacy.html"
}
Write-Host "Written $out"
Get-ChildItem $out -Recurse -File | ForEach-Object { "{0,12:N0}  {1}" -f $_.Length, $_.FullName.Substring($out.Length + 1) }
