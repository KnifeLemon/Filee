# Conversion engines

Filee chooses engines automatically. Settings → *Engines* shows their status, lets you change the priority
(higher wins when two engines can do the same conversion), and install or remove the optional engines.

| Engine | How it gets there | Handles |
|---|---|---|
| ImageMagick (Magick.NET) | built in (library) | JPG, PNG, WEBP, TIFF, BMP, GIF, ICO, AVIF, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM ↔ each other; HEIC, XCF, camera RAW (LibRaw) and EMF/WMF (Windows) read |
| Vector graphics | built in (Svg.Skia, SkiaSharp) | **SVG/SVGZ → PDF (vector) and images**, SVG ↔ SVGZ, PDF-compatible AI → PDF |
| ICNS | built in | images → ICNS (16–1024 px), ICNS → images |
| PDFsharp | built in (library) | images → PDF, merge, split, page ranges |
| PDFium | built in (library) | PDF → images (multi-page TIFF/GIF supported) |
| Unhwp | built in (library) | HWP/HWPX → TXT, Markdown, HTML |
| Markdown (Markdig) | built in (library) | **Markdown → HTML, TXT** (and → PDF / DOCX through the HWPX writer, rhwp or LibreOffice) |
| Spreadsheets (ExcelDataReader for XLS) | built in (library) | **XLSX, XLS, ODS, CSV, TSV → XLSX, ODS, CSV, TSV** (one CSV / TSV per sheet) |
| Office Open XML | built in | **DOCM / DOTX / DOTM ↔ DOCX, XLSM / XLTX ↔ XLSX, PPTM / POTX / PPSX ↔ PPTX** (macros removed for macro-free types) |
| Fonts | built in | **TTF, OTF, WOFF, WOFF2, EOT ↔ each other**; CFF (PostScript) outlines become TrueType for TTF and EOT |
| **HWPX writer** | built in | **DOCX (+ DOCM/DOTX/DOTM), XLSX (+ XLSM/XLTX), XLS, ODS, CSV, TSV, PPTX (+ PPTM/POTX/PPSX), PDF, TXT, Markdown → HWPX** (and with rhwp → PDF and images); HTML, ODT, RTF, reStructuredText, LaTeX → HWPX with Pandoc |
| **DOCX writer** | built in | **Markdown, TXT, XLSX, CSV, PPTX (+ variants), PDF → DOCX** |
| PDF text | built in (PdfPig) | **PDF → TXT** (reading order, also two columns; no OCR) |
| E-mail | built in (MimeKit) | **EML → HTML** (headers + body, inline pictures), **TXT**, **ZIP** (the attachments) |
| CAD (ACadSharp + built-in renderer) | built in (library) | **DWG ↔ DXF**; DWG/DXF → **PDF** (vector), **SVG**, PNG, JPG, WEBP, TIFF, BMP, GIF, ICO, AVIF |
| rhwp | bundled with the installer (`engines/rhwp`) | HWP/HWPX → PDF, **HWP → HWPX, HWPX → HWP** |
| Archives (7-Zip) | bundled with the installer (`engines/7zip`, ~2.5 MB) + built-in readers | ZIP, 7Z, RAR, TAR (+ GZ/BZ2/XZ/Z/7Z/LZ), CAB, ISO, DMG, … **→ folder, ZIP, 7Z, TAR, TAR.GZ/BZ2/XZ**; ALZ, EGG, lzip read in-process; "Compress into one archive" for any files |
| LibreOffice + H2Orestart + Java | **optional download** (~420 MB, 1.3 GB on disk) | older and rare formats only: DOC, XLS, PPT, RTF and OpenDocument → PDF and to each other, output as DOCX/ODT/ODS/ODP; HWP/HWPX → DOCX/ODT; read-only formats (see below) → PDF and their kind's editable formats |
| Pandoc | **optional download** (~42 MB, 240 MB on disk) | Markdown ↔ DOCX/ODT/RTF, DOCX/ODT/HTML/RTF → Markdown, HTML ↔ DOCX/ODT, reStructuredText and LaTeX ↔ Markdown/HTML/DOCX/ODT (and → RTF, EPUB, TXT) |
| Ghostscript | **optional download** (~20 MB, 31 MB on disk) | EPS/PS → PDF, PDF → EPS/PS, PostScript-based AI → PDF (images through PDF and PDFium) |
| FFmpeg | **optional download** (~100 MB, 272 MB on disk) | **all video and audio**: video ↔ video, video → animated GIF or a still frame, audio extraction, audio ↔ audio, GIF → MP4/WEBM/MOV |

DOCX, XLSX, XLS, ODS and PPTX → PDF need neither Microsoft Office nor LibreOffice: they are read in-process, written as HWPX
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
- Ghostscript comes from conda-forge (Artifex publishes only an NSIS installer): a `.conda` package is a zip with a
  zstd tarball, of which only `Library/bin` is unpacked (SharpCompress; `fetch-engines.ps1` uses Windows' `tar.exe`).
  Its fonts and resources are compiled into `gsdll64.dll`. The Microsoft C++ runtime it was built against
  (`vcruntime` component) is copied next to `gswin64c.exe`, so it runs on PCs without the VC++ Redistributable.

To bump a version, change `url`, `sha256` and `size` in engines.json together, run
`pwsh build/fetch-engines.ps1`, test, and update `THIRD-PARTY-NOTICES.md`. When the LibreOffice team retires a
version from `download.documentfoundation.org/libreoffice/stable/`, its URL stops working: keep the pinned version
current.

## Images and vector graphics

- **Layered files.** PSD/PSB convert from Photoshop's composite (ImageMagick merges the layers when a file was saved
  without "maximize compatibility"). GIMP XCF layers are composited at their offsets with their blend modes; hidden
  layers are left out. ImageMagick reads 8-bit XCF without zlib tile compression (GIMP 2.10+ "better but slower
  compression" files give a clear error).
- **Camera RAW** (DNG, CR2, CR3, NEF, ARW, …, and `.raw`) is decoded by LibRaw with the camera's white balance,
  sRGB output and AHD demosaicing, then turned upright from the EXIF orientation.
- **EMF/WMF** are rendered through GDI+ (Windows only) at the preset's render DPI (at least 150), keeping their
  physical size in PDF output.
- **SVG → PDF** replays the drawing into Skia's PDF backend: paths, gradients and clips stay vector. Text is written
  as glyph outlines (shaped with HarfBuzz), because SkiaSharp's PDF backend has no font subsetter and would embed
  every font whole (13 MB for one line of Korean). SVG → images is rendered at its final size: the preset's DPI, at
  least 1024 px on the long edge, or exactly the preset's resize target. External files next to the SVG are
  loaded; nothing is fetched from the network and scripts don't run.
- **ICNS** files are written with every size iconutil makes (16–1024 px, PNG), the picture centred on a transparent
  square; reading takes the largest image (PNG, JPEG 2000 or the old RLE + mask entries).
- **Illustrator** files saved with "Create PDF compatible file" (the default since Illustrator 9) are PDFs and need
  nothing extra; PostScript-based ones need Ghostscript.
- EPS, PS and AI reach PNG, JPG, … through PDF (Ghostscript → PDFium), and SVG reaches EPS through PDF.

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
- **XLSX, XLS, ODS, CSV and TSV** (`Office/Sheets`, see [Spreadsheets](#spreadsheets)): every visible sheet becomes a section with one table. Cells show what Excel
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

## Spreadsheets

`SpreadsheetConverter` (engine id `spreadsheet`) converts between XLSX, XLS (also `.xlt`), ODS, CSV and TSV (also
`.tab`) in-process; every format goes through one in-memory workbook (`Office/Sheets/Workbook.cs`) whose cells keep
the text the spreadsheet shows, the value behind it and its Excel number format.

| Format | Read | Written |
|---|---|---|
| XLSX | own reader: values, number formats, fonts, fills, borders, alignment, merges, widths, heights, hidden rows / columns / sheets, frozen rows | every sheet, numbers and dates as numbers with their formats, booleans, styles, merges, widths, heights, hidden rows / columns, frozen rows |
| XLS (Excel 97–2003, also Excel 5/95) | ExcelDataReader (MIT): values, number formats, merges, horizontal / vertical alignment; widths, heights and hidden rows / columns from the BIFF records (`XlsLayout.cs`) | no (LibreOffice) |
| ODS | own reader (`content.xml`, `styles.xml`, `settings.xml`): repeated rows / columns (the empty repeats up to the end of the sheet are skipped), spans, every value type, the text Calc shows (`text:p`), data styles as Excel codes, cell styles, widths, heights, hidden rows / columns / sheets, frozen and header rows | every sheet, typed cells (float, percentage, currency, date, time, boolean) with data styles and the shown text, styles, merges, widths, heights, hidden rows / columns, frozen rows |
| CSV / TSV | delimiter (CSV: comma, semicolon or tab; TSV: tab) and encoding (UTF-8 / UTF-16 with BOM, else CP949) | UTF-8 with BOM, CRLF, one file per sheet |

- Number formats: Excel codes and OpenDocument data styles are converted both ways (`OdsNumberStyles.cs`) for
  digits, decimals, grouping, thousands scaling, percent, scientific, currency symbols, literal text, colours,
  positive / negative / zero sections, dates, times and durations. Codes with conditions (`[>100]`), fractions or
  text between digits are written as plain numbers (General).
- Column widths use Excel's unit (a digit of Calibri 11 = 7 px); in ODS a character is 5.25 pt. LibreOffice
  measures with its own fonts, so a width can differ by up to a fifth after LibreOffice opens the file.
- XLS cells keep the default font, fill and borders (ExcelDataReader does not expose them); XLS output still
  needs LibreOffice. Password-protected XLS and ODS files stop with a clear message.
- Not converted: formulas (their cached results are kept), charts, pictures, comments, conditional formatting,
  data validation, print areas and page breaks.

## Fonts

Fonts are converted in-process (`Filee.Engines/Fonts`); zlib and Brotli come with .NET. The input format is
recognised by content, so a mislabelled file still converts.

- **WOFF 1.0**: every table zlib-compressed (stored as-is when that isn't smaller). Lossless both ways.
- **WOFF2**: one Brotli stream at the highest quality with the glyf/loca transform and, where it applies, the hmtx
  transform, so files come out the size of Google's reference encoder's. Reading handles both transforms, the
  overlap bitmap, untransformed tables and the first font of a WOFF2 collection. Like other encoders, Filee drops
  the DSIG signature and sets bit 11 of head.flags. Brotli is slow on big fonts (about 10 s per MB, so about a
  minute for a 4.6 MB CJK font); the job shows progress and can be cancelled.
- **EOT**: written as version 0x00020001 (names and OS/2 fields in the header, no MicroType Express compression, no
  XOR obfuscation, no URL restriction). Uncompressed EOT files are read (XOR-obfuscated ones too); MTX-compressed
  ones fail with a clear message.
- **TTF → OTF** keeps the TrueType outlines: TrueType-flavoured OpenType is valid OpenType and what systems and
  browsers expect; cubic outlines would gain nothing.
- **OTF with CFF outlines → TTF** (and → EOT, because Internet Explorer renders only TrueType outlines in EOT)
  converts the glyphs: Type 2 charstrings with subroutines, flex, seac accents and CID-keyed fonts are drawn, curves
  are approximated with quadratic splines within 1/1000 em (the approach of fontTools' cu2qu) and contours are
  reversed to TrueType's direction. glyf, loca, maxp 1.0 and post (format 2 with the CFF glyph names, format 3 for
  CID fonts) are built; head, hhea, hmtx and, from VORG, vmtx are updated; CFF, VORG and DSIG are dropped; every
  other table (cmap, name, OS/2, GSUB, GPOS, kern, ...) is kept. PostScript hints are not converted: the font gets a
  gasp table (smoothing at every size) and a prep that switches on dropout control instead.
- Not supported: CFF2 (variable) outlines to TTF or EOT (to WOFF, WOFF2 and OTF they convert), writing font
  collections, and WOFF/WOFF2 extended metadata (dropped).

`tests/Filee.Engines.Tests/FontTests.cs` checks every conversion with small OFL fonts in
`tests/Filee.Engines.Tests/Fonts` (see `OFL.txt` there): round trips keep every table byte for byte, a WOFF2 made
by Google's encoder decodes to the source font, and converted CFF glyphs are compared with the originals in Skia.

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

## HWPX reader

`Hwp/Hwpx/HwpxReader*.cs` is the inverse of the writer: it reads an HWPX package into the same document model, so
HWPX (and HWP, which rhwp turns into HWPX first) can be written as DOCX, HTML or EPUB without Hancom Office or
LibreOffice. It reads files saved by 한글, by rhwp's `export-hwpx` and by Filee itself:

- elements are matched by local name (any prefix or namespace version); `hp:switch` takes the `hp:case` for
  HwpUnitChar / 2016 paragraphs, else `hp:default`, whose lengths 한글 doubles (as are margins and tab stops
  outside a switch in older files); every `…IDRef` is resolved through `header.xml`;
- sections in spine order with page size (`landscape="NARROWLY"` turns the portrait sheet), margins, gutter,
  columns (also changing mid-section), hidden first header/footer, start page (`startNum`, `hp:newNum`);
- headers and footers (both / odd / even pages) with page numbers and counts; an automatic page number
  (`hp:pageNum`, e.g. "- 1 -" at the bottom centre) becomes a footer or header paragraph;
- paragraph shapes (alignment, indents with 한글's hanging-indent convention, spacing, line spacing kinds, tab stops,
  keep options, page break before) and complete character shapes (fonts per language, size, bold/italic,
  underline, strikeout, colour, shade, highlighter `hp:markpen`, super/subscript, letter spacing);
- outline paragraphs (개요) as headings, numbered with the section's outline numbering when it shows numbers;
  numberings and bullets as lists; tabs, line breaks, non-breaking/fixed-width spaces, soft hyphens; tracked
  deletions are left out;
- hyperlinks (HYPERLINK fields, also across paragraphs; `?name` = bookmark), bookmarks, other fields as their
  text, footnotes and endnotes (without 한글's number), equations as their script text;
- tables with spans, column widths, row heights, header rows, borders and fills (`borderFill`), cell padding,
  vertical alignment, captions and nested tables; a table splits the paragraph that holds it;
- pictures from `BinData` (inline, or floating with `hp:pos` offsets, alignment and wrapping), rectangles,
  ellipses and lines with outline and solid / gradient fill, text boxes (`hp:drawText`), groups.

Not kept (the model has no place for them; skipped and counted by the `Read(path, media, skipped)` overload):
OLE objects, charts, video, form controls, text art (its text is kept), arcs / polygons / curves without text,
master pages, memos, character ratio, relative size and offset, paragraph borders, picture cropping and rotation.
Password-protected (encrypted) HWPX and DRM-wrapped files stop with a clear error.

## HWP ↔ HWPX

rhwp converts between the two 한글 formats without Hancom Office (`export-hwpx` and `convert`). Anything → HWP goes
through the HWPX writer and then rhwp.

## Archives

The archive engine (`Archives/ArchiveConverter.cs`, id `archive`) uses Filee's own copy of the 7-Zip console program
(`7z.exe` + `7z.dll`, taken from the official x64 MSI with `msiexec /a` by `build/fetch-engines.ps1 -Only 7zip` and
bundled with the installer), never a 7-Zip installed on the PC. It runs as a separate process (LGPL).

| Format | Read | Write |
|---|---|---|
| ZIP, JAR | 7-Zip | ZIP in-process (System.IO.Compression): UTF-8 names with the language-encoding flag, times, empty folders |
| 7Z | 7-Zip | 7-Zip |
| TAR | 7-Zip | in-process (System.Formats.Tar, PAX: UTF-8 names, times) |
| TAR.GZ | 7-Zip, in two stages | in-process (PAX TAR + GZipStream) |
| TAR.BZ2, TAR.XZ | 7-Zip, in two stages | TAR in-process, compressed by 7-Zip |
| TAR.7Z, TAR.Z | 7-Zip, in two stages | — |
| LZ, TAR.LZ (lzip) | Filee: each member rewritten as a .lzma file for 7-Zip's LZMA decoder, CRC and size checked | — |
| GZ, BZ2, XZ | 7-Zip | 7-Zip; one file only (from another single-file format, or "Compress into one archive" with one file) |
| LZMA, Z | 7-Zip | — |
| RAR 4/5, CAB, CPIO, DEB, RPM, DMG, ISO, IMG, LHA/LZH, ARJ | 7-Zip (an RPM's payload is unpacked until the files appear) | — |
| ALZ (ALZip) | Filee: store, deflate and ALZip's bzip2 variant | — |
| EGG (ALZip) | Filee: store, deflate, bzip2, LZMA (decoded by 7-Zip); several blocks per file | — |

- **Extract** (target `folder`): the archive is unpacked into a hidden folder next to the result and moved into place
  when complete. When the archive holds exactly one top folder, its content becomes the result folder (`photos.zip`
  with `photos/…` gives `photos/…`, not `photos/photos/…`). Name conflicts follow the preset: `name (2)`, overwrite
  (files are merged into the existing folder) or skip.
- **Archive → archive** unpacks into the job's temp folder and packs everything into the target. `Preset.Archive.Level`
  maps to Store / Fastest / Optimal / SmallestSize in-process and to 7-Zip's `-mx0/1/5/9`.
- **Compress into one archive** (`Archive.CombineIntoOne`, the "One ZIP" preset): `JobQueue` hands all dropped files,
  of any type and unconverted, to `IFileCombiner` (implemented by this engine). The archive is named after the first
  file with the output pattern; files with the same name become `name (2).ext`. ZIP, TAR and TAR.GZ need no 7-Zip.
- **Safety**: every archive is listed first. Encrypted entries or archives stop with "password-protected"; entries
  (or hard links) with `..`, a drive or a leading slash stop the job ("points outside the target folder", nothing is
  written); symbolic links are never unpacked and any link left afterwards is deleted; archives with more than
  1,000,000 entries or more data than the drive has free are refused; Filee's own readers also stop when data unpacks
  to more than the entry declares, and check CRCs.
- **Not supported**: encrypted archives, split (multi-volume) ALZ and EGG, solid EGG, EGG entries compressed with
  ESTsoft's own AZO method, LZO / TAR.LZO (lzop; 7-Zip has no reader either), and writing RAR, ALZ or EGG.
- The ALZ and EGG readers are Filee's own code, written from the format descriptions of unalz (zlib licence), the
  `unalz` Rust crate, EggDotNet and unegg (MIT). ALZip's bzip2 has no `BZh` header, starts every block with `DLZ\x01`
  instead of the block magic, CRC and randomised bit, and ends with `DLZ\x02` (`Archives/Bzip2Decoder.cs`).
- Tests (`ArchiveTests.cs`) build ALZ, EGG and lzip files in code (`ArchiveBuilders.cs`, with bzip2 and LZMA streams
  written by 7-Zip) and skip the 7-Zip parts when `engines/7zip` is missing.

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

## FFmpeg

`Media/FfmpegConverter.cs` converts every video format to every video format, to animated GIF, to a still PNG frame
and to every audio format (the sound track); every audio format to every audio format; and GIF to MP4, WEBM and MOV.
Same-format edges exist too, so "MP4 720p" works on MP4 files and "MP3 128k" on MP3 files.

- The download is the gyan.dev **9.0.2 "full_build-shared"** Windows build (GPL-3.0, run as a separate program).
  It is smaller than the "essentials" build (100 vs 115 MB download, 272 vs 329 MB on disk, because ffmpeg.exe and
  ffprobe.exe share their libraries) and has a superset of its encoders. It links only Windows system DLLs; GPU
  and Vulkan libraries are loaded only if present. Being over 60 MB, it is offered but not ticked on first run.
- Container and codecs per target (`Media/MediaEncoding.cs`, every encoder checked with `ffmpeg -encoders`):

  | Target | Written as |
  |---|---|
  | MP4, M4V, MOV, MKV, FLV, TS, M2TS, 3GP, 3G2 | H.264 (x264, preset medium, yuv420p) + AAC; MP4/M4V/MOV/3GP/3G2 with the index at the front (`+faststart`) |
  | WEBM | VP9 (constant quality, speed 4, row multithreading) + Opus |
  | AVI | MPEG-4 part 2 tagged XVID + CBR MP3 |
  | WMV | WMV 8 (wmv2) + WMA 2 |
  | MPG | MPEG-2 program stream: MPEG-2 video + MP2 |
  | VOB | DVD-Video: 720×480 at 29.97 fps (NTSC) or 720×576 at 25 fps (PAL, for 25/50 fps sources), letterboxed into 4:3 or 16:9, AC-3 at 48 kHz, DVD mux rate |
  | OGV | Theora + Vorbis |
  | DV | DV25 with the same NTSC/PAL frames as VOB, 48 kHz stereo PCM |
  | MXF | OP1a with long-GOP MPEG-2 + 48 kHz PCM |
  | GIF | two passes: palettegen over the whole clip, then paletteuse; 15/12/10 fps and at most 640/480/320 px wide (High/Balanced/Small) |
  | MP3 · M4A/M4B · AAC · OGG · OPUS · WEBA · MKA · WMA · MP2 · AC3 · AMR | LAME VBR (V0/V2/V5) or CBR · AAC · AAC (ADTS) · Vorbis · Opus · Opus · Opus · WMA 2 · MP2 · AC-3 · AMR-NB 12.2k (8 kHz mono) |
  | WAV · AIFF · AU · CAF · VOC · FLAC | 16-bit PCM (RF64 for WAV over 4 GB) · FLAC |

- Preset options (`MediaOptions`): *Quality* picks CRF 18/23/28 (x264), 24/32/38 (VP9), quantizer 2/4/7 (MPEG-4,
  WMV, MPEG-2), Theora 8/6/4 and the audio bitrates (e.g. AAC 256/192/128 kbit/s per stereo pair); *MaxHeight*
  scales down only, keeps the aspect ratio and even sizes; *AudioBitrateKbps* overrides the audio bitrate (snapped to
  the MP3/MP2/AC-3 bitrate tables); *RemoveAudio* drops the sound.
- The first video stream (cover pictures excluded) and the first audio stream are converted; files without sound
  still convert to video. Title, artist and other tags and chapters are kept, album art too for MP3, M4A, M4B and
  FLAC. Portrait phone videos are turned upright. Odd sizes are evened out, frame rates that MPEG-2/MXF/MPEG-4 part 2
  cannot store are converted, and channels WMA or AMR cannot hold are mixed down.
- Progress comes from `-progress` (position ÷ duration from ffprobe); the GIF palette pass reports per-frame stats of
  a discarded copy of its frames. Cancelling kills the process tree and deletes the partial output. There is no
  time limit, but FFmpeg is stopped when it reports nothing for 10 minutes. At most two conversions run at a time
  (FFmpeg's encoders use every core themselves).
- Video → PNG writes one still frame (a tenth into the video, where the picture has usually faded in); the image
  engines turn it into JPG, WEBP, PDF, …. The GIF edge costs more, so these routes never go through an animated GIF
  of the whole video. Images and PDFs → MP4/WEBM/MOV are possible through GIF (a slideshow of the PDF's pages, a
  single frame for a photo).
