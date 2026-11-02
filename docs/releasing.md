# Releasing

How a version of ByteScape is built and published. Everything runs on Windows with the .NET 8 SDK; the Store steps also
need the Windows SDK (makeappx, makepri, signtool, App Certification Kit).

## 1. Version

Change the version in `ByteScape.Core/Branding.cs` (`Version`), in `ByteScape.Core/ByteScape.Core.csproj` (`Version`)
and in `ByteScape.Wpf/ByteScape.Wpf.csproj` (`Version`, `AssemblyVersion`, `FileVersion`). The Store needs four parts
with a trailing zero (`1.2.0.0`).

## 2. Tests

```powershell
dotnet test ByteScape.Tests -c Release
```

Before a release with UI changes, also do the five-minute screen reader check at the end of
[accessibility.md](accessibility.md).

## 3. Portable downloads

```powershell
.\store\build-portable.ps1
```

Writes to `store\out\portable`:

| File | |
|---|---|
| `ByteScape-<version>-x64.exe` | Standalone: .NET inside the exe (compressed, about 67 MB). Runs on any Windows 10 / 11 x64 PC. |
| `ByteScape-<version>-x64-net8.exe` | About 2 MB; uses the .NET 8 Desktop Runtime installed on the PC (or a newer one). |
| `SHA256SUMS.txt` | SHA-256 of both exes. |
| `README.txt`, `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` | For distributing the files elsewhere. |

The exes are built with `DebugType=none`: they carry no debug directory, so no path of the build machine ends up in them.

## 4. GitHub release

The portable exe's update check (`Branding.UpdateUrl`) reads the repository's **latest release**: its tag is the version
(`v1.2.0`), its page is what the program opens, and pre-releases and drafts are ignored.

```powershell
git tag -a v1.2.0 -m "ByteScape 1.2.0"
git push origin v1.2.0
gh release create v1.2.0 --title "ByteScape 1.2.0" --notes-file notes.md `
    store\out\portable\ByteScape-1.2.0-x64.exe store\out\portable\ByteScape-1.2.0-x64-net8.exe store\out\portable\SHA256SUMS.txt
```

Put the SHA-256 values in the release notes as well.

## 5. Microsoft Store

See [store/STORE_SUBMISSION.md](../store/STORE_SUBMISSION.md): the package with the Partner Center identity
(`store\build-msix.ps1`), the certification kit (`store\run-wack.ps1`), the screenshots (`StoreShotTests`) and the
listing texts in seven languages.

## 6. Optional web site

`store\build-site.ps1` puts a static download site together in `store\out\site` (download page, privacy policy from
`PRIVACY.md`, licence files, screenshots, the portable exes). Any static host can serve it, GitHub Pages included. The
project page and the update check work without it.

## Code signing

The portable exes are not code-signed. Windows SmartScreen shows "Windows protected your PC" for new unsigned downloads
until they have built up reputation; users choose "More info → Run anyway". A code signing certificate removes the warning
(sign with `signtool sign /fd SHA256 /tr <timestamp server> /td SHA256 <exe>` after step 3). The Store package is signed by
the Store.
