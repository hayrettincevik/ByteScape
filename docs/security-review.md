# Security review

Scope: the sensitive surfaces — deleting and cleanup, starting program uninstallers, the registry (Explorer menu), the COM
server — and everything connected to them: files that come from elsewhere (.sns and du snapshots, settings profiles,
resume files), starting other processes, native DLL calls, reports, the local AI layer, and the housekeeping deletes the
program does while it runs as administrator.

Method: manual review of the code (every `Process.Start`, `DllImport`, delete, registry write and place that builds a file
name), reproducing each finding in a test folder (junction swaps, a 3000-level folder chain, tampered files), and a
permanent test for every fix (`ByteScape.Tests/SecurityTests.cs`). Elevated situations were verified through the code and
test hooks.

To report a vulnerability, see [SECURITY.md](../SECURITY.md).

## Threat model

- **Same user, unprivileged code.** Malware running in the user's account can already delete the user's files; ByteScape
  gives it no extra power there. What matters is that ByteScape running **as administrator** ("Restart as administrator",
  needed for the fast MFT scan) cannot be tricked into misusing that right (privilege escalation). The Store app cannot
  elevate itself, so most findings concern the portable exe only.
- **Files from elsewhere**: a .sns file someone sends (opens with a double-click), a settings profile, a folder.
- **Files next to the portable exe**: the exe often runs from Downloads, where a DLL or exe may have been dropped.

## Findings and fixes

| # | Severity | Finding | Fix | Test |
|---|---|---|---|---|
| 1 | **High** (elevated) | **Junction swap race when deleting.** If unprivileged code replaces a folder on the path (e.g. `Temp\a`) with a junction to `C:\Windows\...` between the safety check and the delete, ByteScape running as administrator deletes the files there. Reproduced: the unprotected call really deleted the outside file. | `Core/Cleanup/DeleteGuard.cs`: while an item is deleted, every folder from the drive root to the target is **held open without delete sharing** (it cannot be renamed or replaced meanwhile); no folder below the scanned folder may have turned into a link; the scanned folder must be the same one as at the start of the scan (volume + file id). Permanent deletes work on handles (`SafeFs.DeleteTree`): each item is opened as itself, links are removed as links and never followed; read-only files and long paths included. | `Delete_ThroughAFolderSwappedForAJunction_…`, `PermanentDelete_RemovesLinksWithoutFollowing_…` |
| 2 | **High** (elevated) | **Automatic housekeeping deletes in the user's folders.** At every start, old folders under `%TEMP%\.net\ByteScape` were deleted recursively (open to the same race); error reports, history summaries and resume files were pruned. Started as administrator, this triggered the attack above without a click. | No pruning while elevated (it happens at the next normal start). | code review |
| 3 | **Medium** (high when elevated) | **Path traversal in resume files.** The `Key` field of `ByteScapeResume\*.json` went straight into a file path; a tampered file with `..\..\ProgramData\X\config` or a full path made the program read and **delete** `.json/.tree/.sns/.state` files elsewhere. | A key can only be 16 upper-case hex characters and must equal the file's own name; otherwise the file is ignored. | `KeptScan_WithAKeyThatNamesAnotherPath_IsIgnored` |
| 4 | **Medium–High** (elevated) | **Uninstallers started elevated without a UAC prompt.** The uninstaller of a per-user program lives in the user's own folder; unprivileged code could replace it and wait for ByteScape running as administrator to start it. | While elevated, ByteScape does not start uninstallers itself; Settings > Apps opens instead (it starts the uninstaller as the user and asks for rights itself when needed). | code review |
| 5 | **Medium** | **Program names without a folder** (`explorer.exe`, `MsiExec.exe` in uninstall commands, `rundll32`) are searched by Windows in the program's folder and the current folder first; a fake `explorer.exe` in Downloads would have run on "Open in Explorer". | `Core/Util/SystemTools.cs`: Explorer by its full path; bare names are looked up in the Windows folders only, and the command does not run when they are not found. | `WindowsPrograms_AreStartedByTheirFullPath_…` |
| 6 | **Medium** | **DLL planting.** `crypt32.dll` and `cldapi.dll` are not on Windows' known-DLL list; since .NET looks in the program's folder first, a fake DLL in Downloads would have run inside ByteScape. | `DefaultDllImportSearchPaths(System32)` in the Core and UI assemblies: Windows DLLs load from System32 only. | `NativeCalls_LoadWindowsDllsFromSystem32Only` |
| 7 | **Medium** | **Network credential leak from a snapshot.** Cloud folder detection asked Windows' Cloud Files API about the paths in a .sns file; opening a .sns with `\\attacker\share` made Windows connect to that server and present the user's NTLM hash. | Network paths (`\\…`) are never queried (sync roots can only be local anyway). Snapshot views were also checked for any other file system access (open, properties, shell menu, delete are off). | `Snapshot_WithANetworkPath_IsNeverLookedUpOnTheNetwork` |
| 8 | **Medium** (denial of service) | **Crash on deep folder chains and tampered .sns files.** More than 30 recursive walks run over the tree; a real 3000-level folder chain **ended the program at once with a stack overflow** (which cannot be caught) when a snapshot was saved. A malicious .sns would have done the same on a double-click. | Snapshot read/write, permanent delete and the developer-leftovers walk are iterative; scanning goes at most **256 levels** deep (deeper folders show as unreadable and are logged once; real trees stay within a few dozen levels); deeper .sns / resume files / du output are refused. The test runs every walk at that depth with **half** of the UI thread's stack. 400 corrupted .sns files: always a clean error, no crash or hang. | `TreeAtTheDepthLimit_…`, `Scan_StopsAtTheDepthLimit_…`, `Snapshot_TooDeepOrDamaged_…` |
| 9 | **Medium–Low** | **A settings profile could switch the local AI on silently.** A shared profile can carry `aiEnabled` and its own server address; after an import, folder and file names would have gone to that address. | When a profile turns the local AI on or changes its address, the address is shown and the user is asked first (default: No); the rest of the profile is imported. | `SettingsProfile_ThatSwitchesTheLocalAiOn_…` |
| 10 | **Low** | **CSV formula injection.** A file named like `=WEBSERVICE(…)` could run as a formula when a CSV report is opened in a spreadsheet. | Text cells starting with `=`, `+`, `-` or `@` get a leading `'` (OWASP advice); numbers are unchanged. In all four CSV writers. | `CsvCells_ThatASpreadsheetWouldRun_…` |
| 11 | **Low** | **Catastrophic backtracking in wildcard patterns.** A filter like `*a*a*a*a…b` (typed, or from the local AI) could take exponential time on a long name and freeze filtering. | Patterns with three or more stars are matched by the non-backtracking (linear) regex engine. | `Wildcard_WithManyStars_…` |
| 12 | **Low** | **The Explorer menu could be added to the administrator account.** Elevated with a different administrator account, the menu would have linked an exe the everyday account can change into the administrator's Explorer. | While elevated the menu is not added (removing is allowed); the user is told to start ByteScape normally. | code review |
| 13 | **Low** (hardening) | Steam ids went unchecked into `steam://` and store links. | Only all-digit ids become links. | code review |

## Reviewed and found sound

- **Registry**: only ByteScape's own three menu keys under `HKCU\Software\Classes` are written, and only those are removed;
  only when the user asks.
- **COM server** (`-ShellMenu`): starts only its own exe with the selected paths through `ArgumentList` (correct quoting);
  accepts no commands; exits after 30 seconds idle.
- **Paths passed to Explorer**: Windows does not allow `"` in file names, so there is no argument injection.
- **"Copy delete commands"** (PowerShell): paths in single quotes with `'` escaped; a real test confirmed that Windows
  PowerShell 5.1's `Remove-Item -Recurse` does not enter a junction (the file at its target was kept).
- **HTML report**: every value escaped, no script in the page. **XML import**: .NET's default refuses DTDs (no XXE).
- **Export templates**: produce text only; no file, process or registry access.
- **Delete rules**: links are never followed; drive roots, the running Windows, profile roots and the Recycle Bin are
  always protected.
- **Local AI**: no connection while it is off; the model's answer only goes through the filter parser (it runs nothing);
  the key is encrypted with DPAPI. **Problem report**: sends nothing itself, names are hidden, the key is never included.
- **Delete probe** (`AccessProbe`): never deletes, only tries; walks the in-memory tree without recursion.

## Update check

For the portable exe (`Core/Update/UpdateCheck.cs`), designed with security and privacy in mind: it runs only when the
user asks or switched on the weekly check (off by default); one request goes to an https address only, with no header but
the program's name and version (verified by a test: no cookies, no identity); at most 64 KB is read, and the check gives up
after 15 seconds; the program **downloads and runs nothing** — the new version's page opens in the browser. The link in the
description can only be https (local files, `file:`, `javascript:`, `ms-settings:` and other links are refused) and is
checked again right before it opens; pre-releases and drafts are never offered. A description may carry the exe's
SHA-256; the window shows it so the user can compare it with the downloaded file. Tests: `ByteScape.Tests/UpdateTests.cs`
(fake server, timeout, 404, overlong and corrupted answers, unsafe links).

## Accepted risks

- **Unsigned exe**: SmartScreen warnings and antivirus suspicion until the portable exe is code-signed. The Store package
  is signed by the Store.
- **Files written while elevated**: settings, log, error reports and disk history still go to a folder the user can change
  (next to the portable exe, or `%LOCALAPPDATA%\ByteScape`). A link there could make these **fixed-name** files (e.g.
  `ByteScapeConfig.xml`, `crash-….txt`) appear in another folder; the attacker controls neither name nor content, and no
  known abuse follows. Advice: run ByteScape as administrator only when the MFT scan is needed.
- **"Follow links" setting** (off by default): with it on, links below the scanned folder are expected and the delete guard
  does not refuse them.
- **Local AI address** may be `http://` (unencrypted); it is the user's own server, and `localhost` is recommended.
- **Very large folders and snapshots** use memory (10 million files ≈ 2 GB model); this is inherent.
