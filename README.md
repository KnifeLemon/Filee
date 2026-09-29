<p align="center">
  <a href="https://filee.sh"><img src="docs/media/banner-en.jpg" alt="Filee: Drag. Drop. Converted." width="100%"></a>
</p>

<p align="center">
  <b>A free, open-source file converter for Windows that you use by dragging.</b><br>
  Hold <kbd>Ctrl</kbd>, drag files and drop them on a format in the donut that opens right at your cursor.
</p>

<p align="center">
  <a href="https://filee.sh"><b>Website</b></a> ·
  <a href="https://github.com/KnifeLemon/Filee/releases/latest/download/Filee-win-Setup.exe"><b>Download</b></a> ·
  <a href="#get-started">Get started</a> ·
  <a href="#documentation">Docs</a>
</p>

<p align="center">
  <a href="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml"><img src="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/KnifeLemon/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/KnifeLemon/Filee?color=7F77DD&label=release" alt="Latest release"></a>
  <a href="https://github.com/KnifeLemon/Filee/releases"><img src="https://img.shields.io/github/downloads/KnifeLemon/Filee/total?color=1D9E75" alt="Downloads"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="MIT license"></a>
</p>

<p align="center"><b>English</b> · <a href="README.ko.md">한국어</a> · <a href="README.zh-CN.md">简体中文</a></p>

<table>
  <tr>
    <td width="33%" valign="top"><b>Right at your cursor</b><br>No app window to open. The formats appear where you already are, only the ones that make sense for the files you drag.</td>
    <td width="33%" valign="top"><b>HWP & HWPX without Hancom Office</b><br>A built-in HWPX writer keeps the layout of Word documents; rhwp converts HWP ↔ HWPX, PDF and PNG.</td>
    <td width="33%" valign="top"><b>Stays on your PC</b><br>Nothing is uploaded. Filee works offline and saves the result right next to the original.</td>
  </tr>
</table>

## See it in action

<p align="center">
  <a href="https://filee.sh"><img src="docs/media/demo-en.webp" alt="Dragging trip.jpg with Ctrl held: a donut of formats opens at the cursor, the file is dropped on PNG, a progress ring completes and trip.png appears" width="820"></a><br>
  <sub>Watch the 27-second video and try the interactive demo on <a href="https://filee.sh">filee.sh</a></sub>
</p>

## Features

- **Donut toolbar at the cursor.** Modifier + drag files (default <kbd>Ctrl</kbd>). Everything is configurable: any modifier
  combination, mouse button, drag distance, a hold gesture or a keyboard shortcut for the selected files.
- **One toolbar per file type.** Images, PDF, office documents, HWP/HWPX and a fallback for mixed files.
  Arrange presets on a live donut by drag & drop; right-click or ✎ edits a preset.
- **Presets.** Quality, resizing, DPI, metadata, TIFF compression, ICO sizes, PDF merge and split, page ranges,
  output folder, file name pattern and conflict handling.
- **Batch conversion.** 1 file or 100, converted in parallel; one failure never stops the rest.
- **Planned routes.** Multi-step conversions are found automatically (e.g. HWPX → PDF → PNG), each step with the engine
  that does it best.
- **Four ways in.** The drag gesture, Explorer right-click and Send To, a keyboard shortcut on the Explorer selection,
  and the drop zone in the main window.
- **Soft, animated UI.** Light, dark or system theme, your accent colour, adjustable roundness and a *Reduce animations*
  switch for older PCs.
- **English, 한국어, 简体中文**, following your Windows language by default.

## Formats

| From | To |
|---|---|
| **Images**: JPG, PNG, WEBP, TIFF, BMP, GIF, ICO, AVIF, HEIC (read) | any image format, PDF |
| **PDF** | PNG, JPG, TIFF, merge, split, page ranges |
| **Documents**: DOCX, DOC, ODT, RTF | PDF, HWPX, each other, TXT, HTML |
| **HWP**: HWP, HWPX | PDF, HWPX ↔ HWP, DOCX, TXT, Markdown, HTML |
| **Spreadsheets & slides**: XLSX, XLS, ODS, CSV, PPTX, PPT, ODP | PDF and each other |
| **Text**: TXT, Markdown, HTML | DOCX, HWPX, PDF |

Office documents and Markdown use optional engines (LibreOffice, Pandoc) that Filee offers to download on first start.
How HWPX is written and checked: [docs/ENGINES.md](docs/ENGINES.md#hwpx-writer).

## Screenshots

<table>
  <tr>
    <td width="50%"><img src="docs/media/page-toolbar-en.png" alt="Donut toolbar editor with profiles and presets"></td>
    <td width="50%"><img src="docs/media/page-home-en.png" alt="Home screen with the drop zone and recent conversions"></td>
  </tr>
  <tr>
    <td align="center"><sub>Arrange the donut by drag & drop, one profile per file type</sub></td>
    <td align="center"><sub>Home: drop zone and recent conversions</sub></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/media/page-presets-en.png" alt="Preset editor with quality and output options"></td>
    <td width="50%"><img src="docs/media/page-toolbar-dark.png" alt="Donut toolbar editor in the dark theme"></td>
  </tr>
  <tr>
    <td align="center"><sub>Presets: quality, size, PDF pages, output folder and file names</sub></td>
    <td align="center"><sub>Dark theme</sub></td>
  </tr>
</table>

## Get started

**Install:** download [`Filee-win-Setup.exe`](https://github.com/KnifeLemon/Filee/releases/latest/download/Filee-win-Setup.exe)
(Windows 10/11, 64-bit) or pick a version on the [Releases](https://github.com/KnifeLemon/Filee/releases) page.

- **The installer is small.** Images, PDF, HWP/HWPX and DOCX → HWPX work right away.
- **Large engines are optional.** On first start Filee offers LibreOffice for office documents (~420 MB) and Pandoc for Markdown/HTML (~42 MB). You can install or remove them any time in Settings → Engines.
- **Updates are automatic.** Filee updates itself from GitHub Releases.

macOS support is planned.

<details>
<summary><b>Build from source</b></summary>

Requirements: .NET 10 SDK, Windows 10/11.

```bash
git clone https://github.com/KnifeLemon/Filee.git
cd Filee
pwsh build/fetch-fonts.ps1                       # optional: Noto Sans UI fonts
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # optional engines for development (all: omit -Only, ~475 MB)
dotnet run --project src/Filee.App
dotnet test
```

Settings live in `%APPDATA%\Filee`. Debug builds never touch the Explorer context menu or auto-start.

| Project | What it is |
|---|---|
| `src/Filee.Core` | Formats, presets, toolbar profiles, settings, route planner, job queue. No UI, no OS APIs. |
| `src/Filee.Engines` | One class per conversion engine (`IConverter`), including the HWPX writer. |
| `src/Filee.Platform.Windows` | Explorer integration, context menu, auto-start. |
| `src/Filee.App` | Avalonia UI: donut toolbar, settings window, tray, toasts. |
| `tests/*` | xUnit v3 tests, including headless UI rendering. |

</details>

## Documentation

| I want to… | Start here |
|---|---|
| Understand how the app fits together | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Know which engine converts what, and their licenses | [docs/ENGINES.md](docs/ENGINES.md) |
| Add a new conversion | [docs/ADDING-A-CONVERTER.md](docs/ADDING-A-CONVERTER.md) |
| Translate Filee into my language | [docs/ADDING-A-LANGUAGE.md](docs/ADDING-A-LANGUAGE.md) |
| Send a pull request | [CONTRIBUTING.md](CONTRIBUTING.md) |

## Contributing

Pull requests are welcome. Good first steps are [adding a converter](docs/ADDING-A-CONVERTER.md) and
[adding a language](docs/ADDING-A-LANGUAGE.md). If Filee saves you some clicks, a ⭐ helps other people find it.

## License

MIT. Bundled third-party components keep their own licenses: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<p align="center">
  <a href="https://star-history.com/#KnifeLemon/Filee&Date"><img src="https://api.star-history.com/svg?repos=KnifeLemon/Filee&type=Date" alt="Star history" width="600"></a>
</p>
