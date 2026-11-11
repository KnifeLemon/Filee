# macOS in-app updates

This record keeps the macOS updater work reproducible and separates verified behaviour from work that still needs a
release-device check.

## Requirements

- An installed Filee app updates itself when the existing Update action is chosen; it must not send an installed macOS
  app to a release page.
- The updater selects only the matching `osx-arm64` or `osx-x64` archive and its matching SHA-256 checksum file.
- It accepts only `/Applications/Filee.app` and `~/Applications/Filee.app`, keeps settings and engines untouched,
  restores the existing app when replacement fails, and restarts the new app.
- The existing windows and Linux paths and all visible updater UI remain unchanged.

## 2026-11-10 — Implementation

- Found the cause: `UpdateService` supported only Windows and Linux. On macOS it selected no release asset and
  `UpdateNoticeWindow` treated the installed app as a portable copy, opening the release page instead.
- Added macOS architecture selection, managed-install detection, archive/checksum selection, secure extraction and a
  detached update helper. The macOS installer now supports `--upgrade`, validates the source and destination bundles,
  stages the replacement and rolls back on activation failure. An app in `/Applications` uses the standard macOS
  administrator prompt; the user Applications folder does not need elevation.
- Added unit coverage for both macOS architectures, standard install locations, exact release asset selection,
  macOS archive validation and the helper arguments.

## Validation record

- `sh -n installer/macos/install.sh`: passed.
- `git diff --check`: passed.
- `/usr/local/share/dotnet/dotnet restore Filee.slnx --force-evaluate --disable-parallel`: passed after the original
  parallel restore was cancelled while downloading packages.
- `/usr/local/share/dotnet/dotnet format --verify-no-changes`: passed.
- `/usr/local/share/dotnet/dotnet test tests/Filee.App.Tests/Filee.App.Tests.csproj -c Release --no-restore --filter
  FullyQualifiedName~UpdateServiceTests`: passed — 85 tests passed; one Windows-only Authenticode test was skipped on
  macOS.
- `/usr/local/share/dotnet/dotnet test Filee.slnx -c Release --no-restore`: passed after the completed restore.
- Pending device test: update a copied older Filee build separately from `~/Applications` and `/Applications`; verify
  the matching architecture archive downloads, checksum verification succeeds, the old app is replaced, Filee restarts,
  and a failed/cancelled administrator prompt leaves the old app usable.
