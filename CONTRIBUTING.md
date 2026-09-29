# Contributing to Filee

Thanks for helping! Filee is built to be easy to extend: most contributions touch one file plus a test.

## Setup

1. Install the .NET 10 SDK (the exact feature band is pinned in `global.json`, newer patches roll forward).
2. `pwsh build/fetch-fonts.ps1` (optional, for the Noto Sans UI fonts).
3. `pwsh build/fetch-engines.ps1 -Only rhwp,pandoc` (optional, enables HWP/HWPX → PDF, HWP ↔ HWPX and
   Markdown/HTML). Without `-Only` it also downloads LibreOffice with H2Orestart and Java (~420 MB). Engines in the
   repository's `engines/` folder are picked up by debug builds; users download the large ones from the app.
4. `dotnet run --project src/Filee.App`, `dotnet test`.

Any IDE works (Visual Studio 2022+, Rider, VS Code with C# Dev Kit).

## Where things live

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first. In short:

- **New file format or engine** → `src/Filee.Engines`, see [docs/ADDING-A-CONVERTER.md](docs/ADDING-A-CONVERTER.md).
- **New language** → `src/Filee.App/Assets/i18n`, see [docs/ADDING-A-LANGUAGE.md](docs/ADDING-A-LANGUAGE.md).
- **UI** → `src/Filee.App/Views` (XAML) + `ViewModels` (CommunityToolkit.Mvvm).
- **Domain rules** (routes, output names, settings) → `src/Filee.Core` — no UI or OS code here.

## Code style

- `dotnet format` must pass (CI runs `dotnet format --verify-no-changes`). `.editorconfig` has the rules.
- Comments are in English so every contributor can read them.
  - Every file starts with a short comment saying what it is for.
  - Public types and members get `///` XML docs.
  - Explain *why* for anything non-obvious (workarounds, engine quirks) — see `WordComConverter.cs` for the tone.
- Keep UI strings out of code: add a key to all three `i18n/*.json` files (a test checks they match).
- Prefer small, focused pull requests.

## Tests

- `tests/Filee.Core.Tests` – pure logic (fast).
- `tests/Filee.Engines.Tests` – real conversions on generated input files. Tests needing an engine that is not
  installed skip automatically. Microsoft Word is only used when `FILEE_TEST_WORD=1` is set (it opens real Word
  windows).
- `tests/Filee.App.Tests` – gesture state machine, donut geometry, translations and headless UI rendering.
  Screenshots of every page land in `tests/Filee.App.Tests/bin/<config>/net10.0/screenshots` — attach them to
  UI pull requests.

## Pull requests

1. Fork, create a branch, make your change with tests.
2. `dotnet format && dotnet test`.
3. Open a PR and fill in the template. Screenshots for UI changes, please.

## Releasing (maintainers)

Push a tag `vX.Y.Z`. `.github/workflows/release.yml` builds, bundles the engines, packs with Velopack and publishes
the installer and update packages to GitHub Releases.
