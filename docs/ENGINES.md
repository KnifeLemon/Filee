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
| CAD (ACadSharp + built-in renderer) | built in (library) | **DWG ↔ DXF**; DWG/DXF → **PDF** (vector), **SVG**, PNG, JPG, WEBP, TIFF, BMP, GIF, ICO, AVIF |
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

## CAD drawings (DWG, DXF)

Built in, no AutoCAD or ODA software (`Filee.Engines/Cad`). [ACadSharp](https://github.com/DomCR/ACadSharp) reads
and writes the files; Filee's own renderer draws them with SkiaSharp (one drawing routine for PDF, SVG and images).

- **Reading**: DWG from R14 (AC1014) to the AutoCAD 2018 format (AC1032, still current in AutoCAD 2026); DXF R12 to
  2018, ASCII and binary. DWG R13 and older fail with "version not supported". Korean AutoCAD writes pre-2007 DXF with
  the code page name `ANSI_949`, which ACadSharp 3.8 does not map; Filee rewrites that header in memory so the text
  is decoded as CP949 (`CadFile.OpenDxf`, covered by a test that fails once ACadSharp handles it).
- **Writing** (DWG ↔ DXF): always **AutoCAD 2018 (AC1032)** — the current DWG format (AutoCAD 2018 and later, DWG
  TrueView, BricsCAD, ODA tools); 2018 DXF stores text as UTF-8. ACadSharp's DWG writer is younger than its reader
  and its DXF writer, so DXF is the safer format to hand on. Objects ACadSharp does not model (proxy and AEC objects,
  some 3D and underlay data) are not written; degenerate dimensions it cannot measure are left out of DWG output
  instead of failing the file.
- **What is drawn**: model space seen from the top (Z is dropped). If model space has nothing visible, the active
  layout (paper space) is drawn instead; viewports inside it are not followed. The page is fitted to the extents of
  what is actually drawn — the header's EXTMIN/EXTMAX are ignored, so drawings far from the origin still fill it.
  - PDF: an ISO A3 sheet (A4 or Letter when the preset asks for it), landscape when the drawing is wider than tall,
    10 mm margins (or the preset's margin). Vector paths, text as text with embedded fonts.
  - SVG: the drawing's aspect ratio with a 420 mm (A3) long edge at 96 px/inch, white background, text as `<text>`.
  - Images: the same page rendered at the preset's render resolution (150 dpi → 2480 px on the long edge, at most
    12 000 px / 80 MP), then the preset's image options (resize, grayscale, quality, DPI) as for other images.
- **Entities**: LINE, XLINE/RAY (clipped to the page), LWPOLYLINE and POLYLINE (bulges, constant and tapered widths,
  spline-fit and 3D), CIRCLE, ARC, ELLIPSE (also partial), SPLINE (NURBS from control points, knots and weights, or
  a smooth curve through fit points), TEXT, ATTRIB and MTEXT (justification, rotation, width factor, oblique,
  mirroring; MTEXT word wrap, height/colour/font/width codes, stacked fractions shown as `1/2`, `%%d` `%%c` `%%p`,
  `\U+` and `\M+` escapes), INSERT (base point, scale, rotation, MINSERT arrays, nesting, attributes), DIMENSION and
  TABLE (their anonymous block), LEADER, MULTILEADER (lines, arrows, text or block content), HATCH, SOLID/TRACE,
  3DFACE edges, POINT, MLINE, polyface and polygon meshes (edges).
- **Hatches**: solid fills, gradients as a linear gradient, patterns as their real pattern lines clipped to the
  boundary; a pattern too dense to draw (over 20 000 segments) becomes a light tint of its colour.
- **Colours and lines**: ACI index table and true colour, BYLAYER / BYBLOCK (inside blocks, entities on layer "0"
  follow the insert's layer). White and near-white (ACI 7, which AutoCAD shows white on black) draw black on the white
  page. Line weights are plotted in millimetres ("Default" as a thin 0.18 mm line); line types become dash patterns
  scaled by LTSCALE, the entity's scale and the block scale (shapes and text inside complex line types are omitted).
- **Layers**: off, frozen and non-plotting layers (including Defpoints) are not drawn, like a plot. A frozen layer
  hides a whole block reference; an OFF one only its layer-0 contents.
- **Fonts**: drawings name SHX fonts, which are not available, so text uses the text style's TrueType font when it is
  installed (arial.ttf, malgun.ttf ...) and otherwise a sans-serif face (Noto Sans, Arial, Liberation Sans ...).
  Characters the font lacks fall back per character to a CJK font (Noto Sans KR/CJK, Malgun Gothic, Apple SD Gothic
  Neo, ...). These come from the fonts SkiaSharp finds on the system; without any, SkiaSharp's default typeface is
  used and the geometry is unaffected.
- **Not drawn** (counted per type in the log): 3DSOLID, REGION and BODY (ACIS), IMAGE, OLE objects, PDF/DWF/DGN
  underlays, WIPEOUT (its masking is not applied), SHAPE, TOLERANCE, proxy entities. MTEXT columns, tabs, underline
  and overline are ignored.
- **Limits**: 1 000 000 drawn primitives, block nesting depth 16, 10 000 MINSERT cells, 2 000 000 hatch pattern
  segments in total; beyond that the drawing is cut short with a warning in the log. Damaged or truncated files fail
  with "The file is not a valid DWG/DXF drawing or is damaged: …".

## HWP ↔ HWPX

rhwp converts between the two 한글 formats without Hancom Office (`export-hwpx` and `convert`). Anything → HWP goes
through the HWPX writer and then rhwp.

## LibreOffice

- Each parallel worker gets its own profile under `%APPDATA%\Filee\libreoffice-profiles` (LibreOffice silently
  fails when two conversions share a profile).
- HWP/HWPX import comes from the H2Orestart extension, which is written in Java; Filee points LibreOffice at the
  downloaded JRE (`-env:UNO_JAVA_JFW_JREHOME`).
