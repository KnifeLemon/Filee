# Conversion engines

Filee chooses engines automatically. Settings → *Engines* shows their status, lets you change the priority
(higher wins when two engines can do the same conversion), and install or remove the optional engines.

| Engine | How it gets there | Handles |
|---|---|---|
| ImageMagick (Magick.NET) | built in (library) | JPG, PNG, WEBP, TIFF, BMP, GIF, ICO, AVIF ↔ each other; HEIC read |
| PDFsharp | built in (library) | images → PDF, merge, split, page ranges |
| PDFium | built in (library) | PDF → images (multi-page TIFF/GIF supported) |
| Unhwp | built in (library) | HWP/HWPX → TXT, Markdown, HTML |
| Markdown (Markdig) | built in (library) | **Markdown → HTML, TXT** (and → PDF / DOCX through the HWPX writer, rhwp or LibreOffice) |
| Spreadsheets | built in | **XLSX ↔ CSV** (one CSV per sheet) |
| **HWPX writer** | built in | **DOCX, XLSX, CSV, PPTX, TXT, Markdown → HWPX** (and with rhwp → PDF and images); HTML, ODT, RTF → HWPX with Pandoc |
| rhwp | bundled with the installer (`engines/rhwp`) | HWP/HWPX → PDF, **HWP → HWPX, HWPX → HWP** |
| LibreOffice + H2Orestart + Java | **optional download** (~420 MB, 1.3 GB on disk) | older and rare formats only: DOC, XLS, PPT, RTF and OpenDocument → PDF and to each other, output as DOCX/ODT/ODS; HWP/HWPX → DOCX/ODT |
| Pandoc | **optional download** (~42 MB, 240 MB on disk) | Markdown ↔ DOCX/ODT/RTF, DOCX/ODT/HTML/RTF → Markdown, HTML ↔ DOCX/ODT |

DOCX, XLSX and PPTX → PDF need neither Microsoft Office nor LibreOffice: they are read in-process, written as HWPX
and rendered by rhwp. LibreOffice's cost is set so the built-in route always wins where one exists.

Filee never uses software installed on the system (Microsoft Office, an installed LibreOffice, programs on `PATH`):
every engine is built in, bundled or downloaded into Filee's own folder, so a conversion behaves the same on every
PC. Running from source, the repository's `engines/` folder (filled by `build/fetch-engines.ps1`) is used.

## Optional engines

The installer contains only the small engines. On first start Filee asks which of the large ones to download;
they can be installed or removed later in Settings → *Engines*. A conversion that needs a missing engine shows
"Needs LibreOffice …" on its donut slice.

- Downloads are pinned in `src/Filee.Engines/Infrastructure/engines.json` (URL, SHA-256, size). The same file is
  read by `build/fetch-engines.ps1`, so development and the app always use the same versions.
- `EngineInstaller` verifies the SHA-256 before unpacking and unpacks into a staging folder that replaces the
  engine folder only when complete. Redirects to mirrors (even plain HTTP) are followed, because the hash decides.
- Engines go to `%LOCALAPPDATA%\Filee\engines`: outside the app folder, so updates keep them, and inside Filee's
  install root, so uninstalling removes them.
- LibreOffice comes as an MSI and is unpacked with an administrative install (`msiexec /a`): files only, no
  registry entries, no admin rights. Help, gallery and most dictionaries are removed afterwards (~500 MB).

To bump a version, change `url`, `sha256` and `size` in engines.json together, run
`pwsh build/fetch-engines.ps1`, test, and update `THIRD-PARTY-NOTICES.md`. When the LibreOffice team retires a
version from `download.documentfoundation.org/libreoffice/stable/`, its URL stops working: keep the pinned version
current.

## HWPX writer

Filee writes OWPML (HWPX) itself, so saving as HWPX needs no Hancom Office and shows no approval dialog.
Readers turn the source into a small document model (`Hwp/Hwpx/HwpxModel.cs`) and `HwpxWriter` writes the package:

- **DOCX** is read directly (`Hwp/Hwpx/Docx/DocxReader*.cs`) so the layout survives:
  - page size, orientation and margins per section; columns, also when they change mid-page;
  - headers and footers (odd/even pages, hidden on the first page) with page numbers and page counts;
  - paragraph formatting (alignment, indents, spacing, line spacing, tab stops, keep with next);
  - character formatting (fonts incl. theme fonts, size, colour, highlight and shading, bold/italic/underline/strike, super/subscript, spacing);
  - lists with Word's numbering formats (1., 가., ①, i., bullets);
  - tables with merged cells, column widths, borders and shading from the table style and cells, repeated header rows;
  - pictures inline and floating, text boxes, rectangles/ellipses/lines with solid or gradient fills, groups;
  - footnotes, endnotes, bookmarks, hyperlinks (web and within the document, e.g. a table of contents);
  - Word's document grid ("lines"), which 한글 does not have, as an "at least" line spacing.
- **XLSX and CSV** (`Office/Sheets`): every visible sheet becomes a section with one table. Cells show what Excel
  shows (number formats via ExcelNumberFormat, dates, percentages, cached formula results), with fonts, fills,
  borders, alignment and merged cells; frozen top rows repeat on every page. The page is A4 with narrow margins,
  landscape when the columns are much wider than portrait, and the table is scaled to the page width like
  Excel's "Fit all columns on one page". CSV is read with its delimiter (comma, semicolon, tab) and encoding
  (UTF-8 / UTF-16 with BOM, else CP949 as saved by Korean Excel). Not converted: charts, pictures, conditional
  formatting, print areas and page breaks.
- **PPTX** (`Hwp/Hwpx/Pptx`): one page per slide, sized like the slide, with every object floating at its place:
  placeholders inherit position and text style from the layout and master, text with bullets and numbering,
  shapes (rectangles, rounded rectangles, ellipses, lines) with solid or gradient fills and outlines, pictures,
  tables with PowerPoint's default table style, groups, master decorations and the slide background. Not
  converted: charts, SmartArt, rotation, effects (shadows, glow), picture cropping, animations, notes and hidden
  slides.
- **TXT** needs no reader (one paragraph per line; UTF-8, UTF-16 or the Korean code page CP949).
- **Markdown** is parsed with Markdig (`MarkdownReader.cs`): headings, emphasis, code, lists (1. / a. / i. with
  start numbers), task lists, quotes, tables with spans, links, images, footnotes and math. No Pandoc needed.
- **HTML, ODT, RTF** are parsed by Pandoc into its JSON AST (`PandocAstReader.cs`, following
  [pypandoc-hwpx](https://github.com/msjang/pypandoc-hwpx)).
- Markdown and Pandoc keep structure only, so page setup comes from the built-in template (A4).

Element order and attribute values follow files saved by 한글 where the schema and 한글 disagree, for example:

- `hp:default` repeats paragraph margins and fixed line spacing doubled (`hp:case` holds the real values);
- page width/height are always those of the portrait sheet, `landscape="NARROWLY"` turns it;
- 한글 keeps a hanging paragraph's first line at `left` and indents the other lines;
- object offsets are unsigned: objects reaching into the margin are anchored to the paper instead;
- layout caches (table row heights, inline tab widths) are estimated, because simpler readers use them as-is.

Every generated file is checked in the tests with three independent readers: rhwp (renders it), Unhwp (reads the
text) and LibreOffice + H2Orestart (opens it), see `tests/Filee.Engines.Tests/DocxToHwpxTests.cs` and
`OfficeTests.cs`.

Not converted: Word charts, SmartArt, equations (kept as text), free-form shapes, tracked changes and comments.

## HWP ↔ HWPX

rhwp converts between the two 한글 formats without Hancom Office (`export-hwpx` and `convert`). Anything → HWP goes
through the HWPX writer and then rhwp.

## LibreOffice

- Each parallel worker gets its own profile under `%APPDATA%\Filee\libreoffice-profiles` (LibreOffice silently
  fails when two conversions share a profile).
- HWP/HWPX import comes from the H2Orestart extension, which is written in Java; Filee points LibreOffice at the
  downloaded JRE (`-env:UNO_JAVA_JFW_JREHOME`).
