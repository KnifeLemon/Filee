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
| Spreadsheets | built in | **XLSX ↔ CSV** (one CSV per sheet), XLSM / XLTX → CSV |
| Office Open XML | built in | **DOCM / DOTX / DOTM ↔ DOCX, XLSM / XLTX ↔ XLSX, PPTM / POTX / PPSX ↔ PPTX** (macros removed for macro-free types) |
| **HWPX writer** | built in | **DOCX (+ DOCM/DOTX/DOTM), XLSX (+ XLSM/XLTX), CSV, PPTX (+ PPTM/POTX/PPSX), PDF, TXT, Markdown → HWPX** (and with rhwp → PDF and images); HTML, ODT, RTF, reStructuredText, LaTeX → HWPX with Pandoc |
| **DOCX writer** | built in | **Markdown, TXT, XLSX, CSV, PPTX (+ variants), PDF → DOCX** |
| PDF text | built in (PdfPig) | **PDF → TXT** (reading order, also two columns; no OCR) |
| E-mail | built in (MimeKit) | **EML → HTML** (headers + body, inline pictures), **TXT**, **ZIP** (the attachments) |
| rhwp | bundled with the installer (`engines/rhwp`) | HWP/HWPX → PDF, **HWP → HWPX, HWPX → HWP** |
| LibreOffice + H2Orestart + Java | **optional download** (~420 MB, 1.3 GB on disk) | older and rare formats only: DOC, XLS, PPT, RTF and OpenDocument → PDF and to each other, output as DOCX/ODT/ODS/ODP; HWP/HWPX → DOCX/ODT; read-only formats (see below) → PDF and their kind's editable formats |
| Pandoc | **optional download** (~42 MB, 240 MB on disk) | Markdown ↔ DOCX/ODT/RTF, DOCX/ODT/HTML/RTF → Markdown, HTML ↔ DOCX/ODT, reStructuredText and LaTeX ↔ Markdown/HTML/DOCX/ODT (and → RTF, EPUB, TXT) |

DOCX, XLSX and PPTX → PDF need neither Microsoft Office nor LibreOffice: they are read in-process, written as HWPX
and rendered by rhwp. Anything the built-in readers understand (including PDF) is written as DOCX by the DOCX writer.
LibreOffice's and Pandoc's costs are set so the built-in route always wins where one exists.

All built-in document readers are registered in one place, `Hwp/Hwpx/DocumentReaders.cs` (format id → reader, and
which formats need Pandoc). The HWPX writer and the DOCX writer take their input formats from it: adding a reader
there is one line and makes both writers (and every route through them) accept the format.

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
- **PDF** is read with PdfPig (see *PDF reader* below); DOCM / DOTX / DOTM, XLSM / XLTX and PPTM / POTX / PPSX
  are read like DOCX, XLSX and PPTX.
- **HTML, ODT, RTF, reStructuredText, LaTeX** are parsed by Pandoc into its JSON AST (`PandocAstReader.cs`, following
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

## DOCX writer

The counterpart of the HWPX writer: `Hwp/Hwpx/Docx/DocxWriter*.cs` writes WordprocessingML by hand from the same
document model, so every reader in `DocumentReaders` can produce an editable Word file without Word, LibreOffice or
Pandoc. It writes:

- sections with page size, orientation, margins (header and footer distances), columns, page number restarts;
  columns that change mid-page become continuous section breaks, as Word does it;
- headers and footers (odd/even pages, "different first page") with PAGE / NUMPAGES fields; a section without its
  own header gets an empty one, since Word would otherwise repeat the previous section's;
- paragraphs: alignment, indents (first line / hanging), spacing, line spacing (the model's percent of the font
  size is converted back to Word's multiple, the inverse of DocxReader), keep with next / keep lines / widow
  control, tab stops with leaders, page and column breaks;
- runs: Latin and East Asian fonts, size, bold, italic, underline, strike, colour, highlight (Word's named colours)
  or shading, super/subscript, letter spacing;
- headings as Word's built-in "heading 1".."heading 9" styles (outline levels, so the navigation pane and tables of
  contents work), lists as real numbering definitions (1., a., 가., ①, bullets, start numbers, nine levels);
- tables with column widths, horizontal and vertical merges, borders, fills, cell alignment, row heights and
  repeated header rows;
- pictures inline and floating (anchored to page, margin, column or paragraph, with wrapping), text boxes and
  rectangles / rounded rectangles / ellipses / lines as Word 2010 shapes (wps) with solid or gradient fills and
  outlines, hyperlinks (web and to bookmarks), bookmarks, footnotes and endnotes.

Parts, relationships and content types are written per part (pictures and links in headers and notes are related
from their own part), children in schema order. The tests validate every file with the Open XML SDK schema
validator, read it back with DocxReader (DOCX → model → DOCX → model keeps the layout) and convert it with
LibreOffice as an independent reader, see `tests/Filee.Engines.Tests/DocxWriterTests.cs`.

Its edges cost 8, less than Pandoc's and LibreOffice's direct conversions, so Markdown → DOCX, PDF → DOCX, ... use
it. DOCX → DOCX is no edge; the Word variants are the Office Open XML engine's job.

## PDF reader

`Pdf/PdfDocumentReader*.cs` reads PDFs with PdfPig (Apache-2.0) into the document model, for PDF → DOCX, HWPX and
(with `PdfTextConverter`) TXT:

- words with their positions, fonts, sizes and colours → lines → paragraphs: a line continues the paragraph above
  when it follows at about one line pitch, unless it starts with a bullet or number, is indented as a first line,
  or the previous line ended although the next word would have fitted; hyphenated line ends are joined, and no
  space is added between Chinese or Japanese lines;
- reading order: gutters between columns are found where no narrow paragraph crosses; paragraphs that span the
  columns (titles, figures, closing text) split the page into bands, read column by column;
- headings from font sizes (short paragraphs clearly larger than the body text, the largest size is level 1), bold
  and italic from the fonts, colours, links from link annotations, alignment, indents, spacing and line pitch;
- pictures: JPEG streams are kept as they are, others are decoded to PNG, and what PdfPig cannot decode is cut out
  of the rendered page; a picture covering the page behind text (the scan under an OCR layer) is skipped;
- page size per page and margins shared by the document; running headers and footers that repeat on most pages
  (and bare page numbers) are left out;
- a page without text (a scan, a page of drawings, a turned page) becomes the rendered page (PDFium, 150 dpi JPEG)
  as a picture behind the text of an empty page, so nothing is lost; there is no OCR. PDF → TXT includes the
  invisible OCR text layer of scanned PDFs that have one.

Password-protected PDFs fail with a clear message. Not converted: tables (their cells come out as paragraphs in
reading order), vector drawings on text pages, form fields and annotations other than links.

## Office Open XML variants

`Office/OoxmlConverter.cs` converts between the variants of a family (Word, Excel, PowerPoint) by copying the
package with the main part's content type rewritten. Going to a macro-free type also removes the VBA project, its
signatures and data, Word's key map customisations and Excel 4.0 macro sheets (with their sheet entries), and every
relationship and content type entry pointing to them. The built-in readers take the variants directly (DOCM → HWPX,
XLSM → CSV, PPSX → DOCX ...).

## E-mail

`Email/EmlConverter.cs` reads .eml files with MimeKitLite (MIT), which decodes quoted-printable and base64, encoded
headers (RFC 2047 / 2231) and charsets including EUC-KR / CP949 and ISO-2022-KR; raw 8-bit header text that is not
UTF-8 is read as CP949, as older Korean mail programs wrote it.

- **EML → HTML**: a standalone page with From / To / Cc / Date / Subject / Attachments and the HTML body (its style
  sheets kept, scripts removed) or the text body; pictures sent inline (`cid:`) become data URIs.
- **EML → TXT**: the same headers and the text body (or the HTML body as text).
- **EML → ZIP**: the attachments (attached messages as .eml) with safe, unique file names.

## HWP ↔ HWPX

rhwp converts between the two 한글 formats without Hancom Office (`export-hwpx` and `convert`). Anything → HWP goes
through the HWPX writer and then rhwp.

## LibreOffice

- Each parallel worker gets its own profile under `%APPDATA%\Filee\libreoffice-profiles` (LibreOffice silently
  fails when two conversions share a profile).
- HWP/HWPX import comes from the H2Orestart extension, which is written in Java; Filee points LibreOffice at the
  downloaded JRE (`-env:UNO_JAVA_JFW_JREHOME`).
- It is the only reader of rarer formats, converted to PDF and to the editable formats of their kind: DOT, Works /
  WPS Writer, WordPerfect, Lotus Word Pro, AbiWord, Apple Pages and StarWriter (→ DOCX, ODT, DOC, RTF, TXT, HTML);
  WPS Spreadsheets (ET), Apple Numbers and StarCalc (→ XLSX, ODS, XLS, CSV, HTML); Apple Keynote, WPS
  Presentation (DPS) and StarImpress / StarDraw (→ PPTX, ODP, PPT); Publisher, CorelDRAW, Visio, CGM and ODG
  (→ ODG, SVG, PNG). The `--convert-to` filter follows the application that opens the file (checked against
  LibreOffice's filter registry): Publisher, CorelDRAW and Visio open in Draw, CGM in Impress, and "SDA" files
  in Draw (.sda) or Impress (.sdd). SVG and PNG export the first page.

## DjVu

Not supported: DjVuLibre (GPL) has no pinned, redistributable Windows build in an archive the installer can unpack.
The official Windows release is an NSIS installer (`DjVuLibre-3.5.29_DjView-4.12_Setup.exe` on SourceForge), which
would have to be run (a system install) or unpacked with 7-Zip; MSYS2's packages are `.pkg.tar.zst` archives with
five dependency packages. A DjVu engine needs a zip of `ddjvu.exe` / `djvutxt.exe` and their DLLs published for
Filee (with the GPL source offer), then an `engines.json` component and an optional package.
