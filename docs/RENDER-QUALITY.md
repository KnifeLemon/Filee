# Render quality harness

How Filee measures a document renderer: GitHub Project 4, "Filee: built-in engines", item *Layout engine: render
quality harness*. Filee's own layout engine doesn't exist yet; the harness measures rhwp today, the renderer Filee
bundles, and the layout engine plugs in at one place when its first milestone lands. No input or reference file is in
the repository: every document is written in code at test time, and real documents stay in a private corpus outside
it.

The code is in `tests/Filee.Engines.Tests/RenderQuality/`.

## Plugging in a renderer

A renderer takes a document and returns a PDF (`DocumentRenderers.cs`):

```csharp
internal delegate Task<byte[]> RenderDocument(RenderInput input, CancellationToken cancellationToken);
internal sealed record DocumentRenderer(string Name, IReadOnlySet<string> Formats, RenderDocument Render);
```

`RenderInput` has the file (HWPX, or a binary HWP), a work folder, and the layout model (`HDocument`, read with
`HwpxReader.ReadForLayout` when first asked for). `DocumentRenderers.Reference` is rhwp as Filee runs it
(`RhwpInput`'s fixes, then `rhwp export-pdf` with Filee's fallback fonts). rhwp is only the yardstick here; nothing in
the harness adds a conversion path through it.

`DocumentRenderers.Candidate` is Filee's layout engine: paragraph layout on an interim page flow
(`Typesetting/FileeRenderer.cs`) until the page composer replaces it. Its PDFs get the same read-back checks and are
compared with rhwp's; set to null, the compare test is skipped.

## The feature matrix

`RenderFeatures.cs` writes one small document per feature. Most are built as a document model and written by Filee's
HWPX writer. Floating objects and equations are built with `DocxBuilder` and go through the DOCX reader, the way a
Word document reaches the renderer. Every document is A4 with 한글's default margins.

| Feature | Built from | What it holds |
|---|---|---|
| `line-spacing-percent`, `-fixed`, `-at-least` | model | 160 %, fixed 20 pt and at least 24 pt line spacing, one paragraph per alignment (left, right, centre, justify, distribute); the lines must be one pitch apart |
| `tabs` | model | left, centre, decimal and right tab stops with dot and dash leaders |
| `lists` | model | numbered list with a 가나다 sub-level and hanging indents, bullets |
| `tables` | model | 3 columns of fixed widths, a filled header row, wrapped cell text, a merged cell |
| `headers-footers` | model | a header, a footer with the page number, two pages |
| `notes` | model | a footnote and an endnote |
| `columns` | model | two columns with a separator line and a column break |
| `floating` | DOCX | a picture with square wrap, a text box above and below the text, a picture behind and a box in front of it |
| `equations` | DOCX | an inline equation, a fraction and a sum |
| `script-ko`, `-zh`, `-ja`, `-ar`, `-he`, `-hi`, `-gu`, `-th` | model | Korean, Chinese, Japanese, Arabic, Hebrew, Devanagari, Gujarati and Thai paragraphs |

`line-spacing-percent`, `tables` and `script-ko` are also rendered from a binary HWP made with `rhwp convert` while
rhwp is bundled (`*.hwp` rows below), and compared with the rendering of their HWPX.

## Read-back checks

Each check is one function in `PdfChecks.cs`. It takes the PDF, plus what the document asks for, and returns a value,
whether the value meets the ideal, and the first offenders. `RenderChecksTests.cs` tests every check on PDFs built in
code.

| Check | Value | Ideal |
|---|---|---|
| `text` | share of the document's text (white space left out, NFC) found in the PDF's text in the same order; `/ActualText` spans count as their text, and text the renderer adds (list numbers, page numbers) doesn't count against it | 1 |
| `overlaps` | share of neighbouring glyphs on a line whose advances overlap by more than 0.05 em; glyphs without an advance (marks) are left out | 0 |
| `outside-page` | glyphs whose outline leaves the page by more than 0.5 pt | 0 |
| `outside-margins` | glyphs whose advance or baseline lies outside the page less its margins by more than 1 pt (the header and footer areas count as inside, as 한글 puts them in the top and bottom margins) | 0 |
| `pages` | the page count | the document's |
| `baselines` | share of lines on their expected baseline within ±0.5 pt (the line-spacing features: one pitch apart from the first line of each page) | 1 |
| `fonts` | fonts that aren't embedded or aren't subset (Type 3 fonts count as both) | 0 |

Lines are letters on one baseline (within 0.35 of their size, so a superscript stays on its line), measured from the
top of the page. Kerning deeper than 0.05 em counts as an overlap. Segoe UI, for example, kerns reh and zain 0.08 em
under a letter that doesn't join them, so a shaping renderer may need a threshold for that (`PdfPageWriterTests`
allows 0.1 em for its Arabic paragraph).

## Differential compare

`RenderDiff.Compare` compares two renderings of one document (rhwp and the layout engine, rhwp's HWP and HWPX, or
한글's PDF and a renderer):

- **pages**: the same page count.
- **lines**: line-break agreement. Each line is its text without white space. The two documents' lines are matched in
  order (longest common subsequence, Myers' O(ND) diff), and a line agrees when the other PDF has a line with the same
  text at that point. It then agrees on both its first and its last character. A page break moved by one line costs
  only the lines that moved. The value is the share of the longer document's lines.
- **placed**: the share of lines that also sit on the same page and baseline (±0.5 pt).
- **ssim**: the lowest per-page SSIM of the pages drawn by PDFium (PDFtoImage) at 100 dpi. SSIM is computed over
  8×8 windows with a step of 4, leaving out windows that are blank paper in both pages (`PageImage.cs`).

Two tests prove the compare itself. **rhwp against rhwp** must be identical (same pages, every line on its baseline,
SSIM 1). **Perturbed renderings** must be caught: the same PDF drawn 2 pt lower keeps every line but no baseline, and
its SSIM falls to 0.27; a copy of the document with a 20 mm wider left margin breaks every line elsewhere.

## Thresholds and the allow-list

`render-thresholds.json` holds what each renderer reaches where it falls short of the ideal, per feature and check.
Every other measurement must meet the ideal. A value worse than its threshold is a regression and fails the test. A
better one passes, and the report says which threshold to raise or remove in the same change. Overlaps are a share,
not a count, so the longer documents of the full run compare with the same thresholds.

The `allow` list holds measurements that are reported but never fail, each with its reason. They are the compares of
the layout engine with rhwp where rhwp is wrong: floating objects, equations, and the scripts it draws without
shaping.

## Running it

- **CI**: the Windows build job runs the harness in its normal `dotnet test` step (rhwp is fetched there). The
  metrics table of each run is added to the job summary (`GITHUB_STEP_SUMMARY`) and to the test output.
  rhwp's results depend on the installed fonts, so the thresholds are those of Windows and the rhwp tests are skipped
  on Linux and macOS. The building blocks run everywhere.
- **`FILEE_RENDER_FULL=1`**: the longer run. The script paragraphs are twelve times as long, a document of some pages
  is added, and every feature is also rendered from a binary HWP. It takes about a minute.
- **`FILEE_RENDER_OUT=<folder>`**: keeps every document, PDF and page picture (PNG at 100 dpi) for a look.
- **Private corpus**: `pwsh build/render-compare.ps1 -Corpus <folder> -Open` (or `FILEE_RENDER_CORPUS`). The folder
  holds documents next to the PDFs 한글 printed of them (`doc.hwpx` + `doc.pdf`, `doc.hwp` + `doc.pdf`). Each renderer
  is compared with 한글's PDF and read back. An HTML report goes to the temp folder, with the page that differs most of
  each document side by side. The corpus run only reports; it never fails on the numbers.
  `HangulLineBreakTests` runs on the same folder: for every HWPX 한글 saved (no PDF needed), the plain-text
  paragraphs are laid out by Filee at the width 한글 used, and their line starts are compared with the ones 한글
  stored (`hp:linesegarray`).

## Slides

The [slide renderer](ENGINES.md#filee-renderer-slides) has its own section of the harness, `SlideQualityTests.cs`
(renderer `filee-slides` in `render-thresholds.json`). `SlideFeatures.cs` writes one deck with `SlideDeckBuilder`: an
Office theme (colour, font and format schemes with fill, line, effect and background styles), a master with text
styles, placeholders and a decorative bar, three layouts, and one slide per feature.

| Feature | What the slide holds |
|---|---|
| `title` | the title layout's centred title and subtitle placeholders, the master's background reference and bar |
| `shapes` | 16 presets with PowerPoint's default shape style (theme fill, line and text colour) and labels, and again with their own fills and 2 pt lines |
| `lines` | straight connectors with every arrowhead, sizes and preset dashes; a double line; bent and curved connectors; a round-capped dashed line; a freeform with lines, an arc, a quadratic and a cubic curve |
| `text` | the content layout's body with bullets on two levels and auto numbers; shrink text on overflow (stored scale and spacing reduction); two columns; right-aligned italic text with a hyperlink |
| `vertical-text` | frames turned 90° and 270°, a shape turned 30° with its text, a mirrored arrow and a flipped triangle |
| `scripts` | Korean, Japanese, Arabic (right to left) and Hindi through the theme's script fonts |
| `pictures` | a picture as it is, cropped, turned with an outline, in an ellipse, grey, half transparent, and tiled into a rounded rectangle |
| `tables` | PowerPoint's default table style (header row, banded rows, a cell merged across) and a table with its own lines and fills |
| `fills` | linear (angle, scaled), circle and rectangle gradients; six patterns; transparent overlapping fills; a turned group; a gradient background |
| `smartart-chart` | SmartArt through its stored drawing, a chart through its fallback picture |

- **Read-back** (`Slide_features_read_back`, always): each slide's text on its page (`/ActualText` included), one page
  per slide, no glyph outside a slide, overlapping glyphs, every font embedded and subset.
- **Against LibreOffice** (`Slide_features_compared_with_libreoffice`): LibreOffice converts the same deck to PDF and
  each slide is compared (`vs-libreoffice-ssim`, PDFium at 100 dpi, as for documents). LibreOffice is looked for in
  `FILEE_LIBREOFFICE` (soffice itself), the repository's `engines/` folder (`pwsh build/fetch-engines.ps1 -Only
  libreoffice`) and the usual install folders; without it the test is skipped. CI doesn't fetch LibreOffice, so there
  the slides get the read-back only. With `FILEE_RENDER_OUT`, the deck, both PDFs and a Filee | LibreOffice picture of
  every slide (`slides-<feature>.png`) are kept.

Measured with LibreOffice 26.2 on Windows 11 (Office fonts installed):

| Slide | SSIM | What differs |
|---|---|---|
| `title` | 0.6275 | the text sits about a point apart |
| `shapes` | 0.9819 | |
| `lines` | 0.8743 | LibreOffice draws the double line as one thick line and arrowheads a little larger |
| `text` | 0.6238 | LibreOffice recomputes "shrink text on overflow" (larger text in the orange box); lines a point or two apart |
| `vertical-text` | 0.9598 | |
| `scripts` | 0.3005 | LibreOffice draws 맑은 고딕 as NanumGothic and Japanese in another face |
| `pictures` | 0.8807 | LibreOffice tiles the picture smaller |
| `tables` | 0.92 | |
| `fills` | 0.8571 | rectangle gradients (an ellipse in Filee), pattern cells |
| `smartart-chart` | 0.9682 | |

The deck read back: every slide's text found, nothing outside a slide, every font embedded and subset, overlaps 0.0063
(Calibri's own kerning of "Tr", an italic "f." and Yu Gothic's "す。").

## rhwp today

rhwp 0.8.7 on Windows, the CI matrix. Every feature not listed meets the ideal in every check. That covers the
three line-spacing rules (lines exactly one pitch apart), tabs, lists, tables, headers and footers, notes, columns,
Korean, Chinese and Japanese, every font embedded and subset, and the HWP renderings of `line-spacing-percent`,
`tables` and `script-ko` identical to their HWPX.

| Feature | Measured |
|---|---|
| `floating` | overlaps 0.1265: square wrap isn't kept, and the two paragraphs after the floating objects are drawn on one line |
| `equations` | overlaps 0.0303: *m* and *c* of the inline E = mc² overlap |
| `script-ar` | overlaps 0.4867 (unshaped, #202) |
| `script-he` | overlaps 0.6 (fixed half-em widths, rhwp#8143) |
| `script-hi` | overlaps 0.6724 (unshaped, #185) |
| `script-gu` | overlaps 0.6404 (unshaped, #185) |
| `script-th` | text 0.9735 (ำ is drawn as its two parts), overlaps 0.214 |

The full run adds the HWP renderings of every feature. From the HWP, rhwp draws 。 and 、 half an em over the next
character, so no Chinese or Japanese line breaks where it does from the HWPX (lines 0, SSIM 0.23 and 0.19), and it
places the equations differently (SSIM 0.50).

Not checked yet: line-breaking rules (kinsoku: in both renderings above, a line of the Chinese paragraph starts with
"，"), colours and pictures beyond SSIM, and tagged PDF.
