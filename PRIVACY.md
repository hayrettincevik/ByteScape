# Privacy

*Applies to the Microsoft Store app and the portable version of ByteScape. Last changed: 2026-11-02.*

**ByteScape does not collect, store or transmit any personal data.** It reads the folders and drives you choose to scan, only to show their sizes on your screen.

## What it reads on your device

- To show how much space each installed program uses, it reads the list of installed programs that Windows keeps (program names, publishers and install folders). The list is used only on your device; it is not stored or sent anywhere.
- To list your games, it reads the local files of the Steam, Epic Games and GOG launchers (installed games, their folders, last played dates and play times). This also stays on your device.
- To list programs you have not used for months, it reads Windows' own record of program starts for your account, and when the programs' data folders last changed. This stays on your device.
- To find duplicate files or compare folders byte by byte, it reads those files on your device. Nothing is sent.

## What it keeps on your device

- Settings are kept in `%LOCALAPPDATA%\ByteScape`; the portable version keeps them next to its exe when that folder is writable.
- For the disk forecast it keeps, next to its settings, the dates, sizes and free space of your drives and the sizes of their largest folders (this can be turned off).
- To continue a stopped scan and to rescan quickly, the last scans of up to four paths (names, sizes and dates of the files and folders) are kept next to its settings for at most a month (both can be turned off).
- When the app meets an error, it keeps a text report of it (the error, the Windows version, the last log lines) next to its settings. At most 20 are kept, and they are never sent.

Files are deleted only when you choose to, after a confirmation, and to the Recycle Bin unless you choose otherwise.

## Network

The app makes no network connections by default.

- The portable version (not the Store app, which the Store updates) looks for a newer version only when you choose "Check for updates" or switch on the weekly check. It asks GitHub for the project's latest release (one request to `api.github.com`), sending nothing but the program's name and version; GitHub's privacy statement applies to that request. A newer version's page opens in your browser; the program itself downloads and installs nothing.
- If you turn on the optional "Local AI" feature and enter the address of a language model server you run yourself, folder and file names and sizes (never file contents) are sent to that address only, to answer your requests. An API key you enter for that server is stored encrypted for your Windows account.
- Links you click (the project page, a new version's page, "Open an issue") open in your browser.

## Problem reports

"Report a problem" packs the system information and, if you choose, the log, the error reports and the settings into a zip file that you save and can look through first. Your Windows user name and computer name are replaced with placeholders unless you choose otherwise; the API key and the recently opened folders are never included. "Open an issue" only opens the project's new-issue page on GitHub in your browser: you decide whether to attach the file and submit it. Issues on GitHub can be read by everyone.

## Contact

Questions about privacy: open an issue at https://github.com/hayrettincevik/ByteScape/issues
