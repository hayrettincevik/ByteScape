# Security policy

ByteScape deletes files when the user asks it to and can run as administrator, so security reports matter a lot.

## Reporting a vulnerability

Please **do not open a public issue**. Report it privately on GitHub: open the repository's **Security** tab and choose
**Report a vulnerability**. Include what an attacker can do, the steps to reproduce it and the ByteScape version.

You will get an answer within a week. Once a fix is released, the report is credited in the release notes unless you
prefer otherwise.

## Supported versions

Only the latest release receives security fixes. The portable version can check for a new release (⋯ → Check for
updates); the Microsoft Store version is updated by the Store.

## What is in scope

- Anything that makes ByteScape delete, open or send something the user did not choose, especially while it runs as
  administrator.
- Crafted files that ByteScape opens (`.sns` snapshots, settings profiles, resume files) leading to code execution,
  file access outside their purpose, network access or a crash.
- DLL or executable planting next to the portable exe.

The review of these surfaces and the accepted risks are described in [docs/security-review.md](docs/security-review.md).
