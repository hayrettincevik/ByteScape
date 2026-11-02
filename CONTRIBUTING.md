# Contributing to ByteScape

Thank you for helping. Bug reports, translation fixes, ideas and pull requests are all welcome.

## Reporting a bug

Use **⋯ → Report a problem** in the program: it saves a zip with the system information, the log and the error reports
(your Windows user name and computer name are replaced) and opens a new issue with the details filled in. Look through the
zip, then drag it into the issue. Issues are public: do not attach anything you do not want others to see.

Security problems: please follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Building and testing

Requirements: Windows 10 or 11 and the .NET 8 SDK.

```powershell
dotnet build ByteScape.sln -c Release
dotnet test ByteScape.Tests -c Release
```

The tests take a few minutes. They open every window off screen and press every command in it, so a change in the UI is
covered as soon as the window exists. They work only in `%TEMP%` and never touch your own files. An overview of the code
is in [docs/architecture.md](docs/architecture.md).

## Pull requests

- Keep a pull request to one topic and describe what changes for the user.
- Add or update tests for what you change; `dotnet test` must pass.
- Match the style of the surrounding code: nullable enabled, comments that explain why, no new dependencies without a
  good reason (the program has none besides .NET).
- Product name, version and project addresses live only in `ByteScape.Core/Branding.cs`.
- Deleting is the most sensitive part of the program: changes to `Core/Cleanup` need a test that shows protected places
  stay protected. See [docs/security-review.md](docs/security-review.md).
- Write texts in your own words, plainly, from the user's point of view.

## Texts and translations

The English text in the code is the key: `T("Text")`, `F($"Text {value}")`, `P(count, "one item", "{0} items")`, and plain
attributes in XAML. Every new or changed English text needs a line in each catalog in
`ByteScape.Core/Core/Localization/` (`tr`, `ru`, `zh-Hans`, `fr`, `es`, `pt-BR`); `LocalizationTests` fail until it has
one. The diagnostics harness lists what is missing and merges translations:

```powershell
dotnet build ByteScape.Diag -c Release
.\ByteScape.Diag\bin\Release\net8.0-windows\ByteScape.Diag.exe locstatus
.\ByteScape.Diag\bin\Release\net8.0-windows\ByteScape.Diag.exe locbatch tr 0 100 todo-tr.txt   # id TAB English
.\ByteScape.Diag\bin\Release\net8.0-windows\ByteScape.Diag.exe locmerge tr done-tr.txt         # id TAB translation
```

Corrections to an existing translation are welcome as a pull request that edits the catalog line, or as an issue.

## Code of conduct

Be kind and constructive; see [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## License

By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE) of the project.
