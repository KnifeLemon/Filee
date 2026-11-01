# Conversion engines

Filee chooses engines automatically. Settings → *Engines* shows their status, lets you change the priority
(higher wins when two engines can do the same conversion), and install or remove the optional engines.

| Engine | How it gets there | Handles |
|---|---|---|
| ImageMagick (Magick.NET) | built in (library) | JPG, PNG, WEBP, TIFF, BMP, GIF, ICO, AVIF, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, MNG, QOI, DDS, PCX, CUR, XPM, XBM, WBMP, PAM, PFM, SGI, Sun Raster, MIFF ↔ each other, and **APNG** (Filee's own reader and writer); HEIC (written with FFmpeg), XCF, camera RAW (LibRaw), DICOM (uncompressed ones by Filee's own reader), **FITS** (Filee's own reader; TIFF at the file's own depth by Filee's own writer), Photo CD, OpenEXR, Radiance HDR, EMF/WMF (drawn by Filee's [metafile renderer](#emf--wmf-renderer)) and the photo of an iPhone LIVP read |
| Windows Imaging Component | part of Windows (Windows only) | **JPEG XR** (JXR, WDP, HDP; HDR screenshots of Windows) → the images ImageMagick writes, and PDF, ICNS, HEIC and SVG through PNG; HDR tone-mapped to SDR |
| Vector graphics | built in (Svg.Skia, SkiaSharp, ACadSharp) | **SVG/SVGZ → PDF (vector) and images**, **EMF / WMF → PDF (vector)** ([metafile renderer](#emf--wmf-renderer)), SVG ↔ SVGZ, **SVG → DXF** (→ DWG with the CAD engine), **raster images → SVG (tracing)**, PDF-compatible AI → PDF, **HP-GL / HP-GL/2 plot files (PLT) → SVG**, PDF and images (pen moves, packed PE polylines, arcs and filled polygons; text labels are not drawn), **Tajima DST embroidery → SVG** (stitches as lines, a palette colour per thread; → PNG, JPG, PDF from there) |
| ICNS | built in | images → ICNS (16–1024 px), ICNS → images |
| PDFsharp | built in (library) | images → PDF, merge, split, page ranges, page organising, lossless rotate and crop-box edits, spreadsheet print-studio assembly, **compression** (smaller PDFs, see below), PDFs without their metadata |
| PDFium | built in (library) | PDF → images (multi-page TIFF/GIF supported) |
| Unhwp | built in (library) | HWP/HWPX → TXT, Markdown, HTML (pictures in a `<name>_files` folder next to the Markdown, embedded in the HTML) |
| Text & Markdown (Markdig) | built in (library) | **Markdown → HTML, TXT**; **TXT, LOG, PY, PAC, BAT, CMD, COMMAND, LUA, CSS, PS1, SQL, AHK, INI, RSC → UTF-8 TXT**, preserving source text without running scripts (and → PDF / DOCX through the HWPX writer, rhwp or LibreOffice) |
| Spreadsheets (ExcelDataReader for XLS) | built in (library) | **XLSX, XLS, ODS, CSV, TSV → XLSX, ODS, CSV, TSV** (one CSV / TSV per sheet) |
| **Spreadsheet printing** | built in | **XLSX (+ XLSM/XLTX), XLS (+ XLT), ODS → PDF** printed the way Excel prints them: page setup, print areas and titles, scaling and fit to pages, page breaks, headers and footers (see [Spreadsheet printing](#spreadsheet-printing)) |
| Office Open XML | built in | **DOCM / DOTX / DOTM ↔ DOCX, XLSM / XLTX ↔ XLSX, PPTM / POTX / POTM / PPSX ↔ PPTX** (macros removed for macro-free types) |
| Fonts | built in | **TTF, OTF, WOFF, WOFF2, EOT ↔ each other**; **FON (NE monochrome FNT v2/v3, supported single-byte charsets) → TTF, OTF, WOFF, WOFF2, EOT only**; CFF (PostScript) outlines become TrueType for TTF and EOT |
| **HWPX writer** | built in | **HWP, DOCX (+ DOCM/DOTX/DOTM), ODT, XLSX (+ XLSM/XLTX), XLS, ODS, CSV, TSV, PPTX (+ PPTM/POTX/POTM/PPSX), PDF, OFD, HTML, MHTML, EPUB, MOBI/AZW3, FB2, HWPX, TXT, Markdown, RTF → HWPX** (and with rhwp → PDF and images); reStructuredText, LaTeX → HWPX with Pandoc |
| **DOCX writer** | built in | **ODT, HWPX, HWP, PDF, OFD, HTML, MHTML, EPUB, MOBI/AZW3, FB2, Markdown, TXT, RTF, XLSX, XLS, ODS, CSV, PPTX (+ variants) → DOCX** |
| **ODT writer** | built in | **DOCX (+ variants), HWPX, HWP, PDF, OFD, HTML, MHTML, EPUB, MOBI/AZW3, FB2, Markdown, TXT, RTF, XLSX, XLS, ODS, CSV, PPTX (+ variants) → ODT** (OpenDocument text, LibreOffice Writer) |
| PDF text | built in (PdfPig) | **PDF → TXT, Markdown** (reading order, also two columns; Markdown keeps headings, emphasis, links and the pictures; no OCR) |
| PDF slides | built in (PDFium, PdfPig) | **PDF → PPTX**: a picture of each page on its own slide (looks like the PDF, not editable), the page's text in the speaker notes |
| PDF tables | built in (PdfPig) | **PDF → XLSX, ODS, CSV** (#267): each table drawn with lines on its own sheet, numbers and dates as real values, merged cells and column widths kept |
| E-mail | built in (MimeKit, MsgReader) | **EML, Outlook MSG → HTML** (headers + body, inline pictures), **TXT**, **ZIP** (the attachments); **MSG → EML**; **MHTML → HTML** (the saved page in one file, pictures inlined; on to PDF, DOCX, HWPX, TXT, Markdown, EPUB from there) |
| Data | built in (System.Text.Json, YamlDotNet, Tomlyn, System.Xml) | **JSON, JSON Lines, YAML, TOML, XML, CSV ↔ each other** (CSV for tables); **vCard, iCalendar ↔ CSV**; FreeMASTER oscilloscope TXT → CSV; spreadsheets, contacts and events reach JSON / YAML / TOML / XML through CSV |
| **E-books** | built in | **EPUB, MOBI/AZW/AZW3/PRC, FB2, HTMLZ, TXTZ → EPUB, HWPX (→ PDF), TXT, HTML, Markdown, FB2, HTMLZ, TXTZ**; HTML, Markdown, TXT, DOCX → EPUB/FB2/HTMLZ/TXTZ; HTML → TXT; comics CBZ/CBR/CB7/CBT/CBC → PDF, EPUB, CBZ; PDF → CBZ; AZW4 → PDF; **EPUB ↔ KEPUB** (Kobo) |
| CAD (ACadSharp + built-in renderer) | built in (library) | **DWG ↔ DXF**; DWG/DXF → **PDF** (vector), **SVG**, PNG, JPG, WEBP, TIFF, BMP, GIF, ICO, AVIF |
| **Subtitles** | built in | **SRT, ASS/SSA, WebVTT, SAMI (SMI), LRC, TTML, MicroDVD, SCC → SRT, ASS, WebVTT, SAMI, LRC, TTML, MicroDVD**, always UTF-8 (Korean CP949 / EUC-KR files read correctly) |
| 3D models (SharpGLTF for glTF) | built in (library) | **GLB, glTF, STL, OBJ (+ MTL), 3MF, PLY ↔ each other**; FBX → each of them; → PNG, JPG, … (an isometric preview) |
| **Geospatial vector data** | built in | **GeoJSON, KML, KMZ, GPX, Shapefile ↔ each other and CSV** (longitude / latitude or WKT); Garmin FIT (activities, read only) → each of them and CSV; projected Shapefiles (UTM, Web Mercator, Lambert, Albers) are converted to WGS 84; through CSV to XLSX, JSON, … (see below) |
| **Filee renderer** (preview) | built in, off until Settings → Engines → *Use the Filee renderer (preview)* | **HWPX, HWP, Word documents, TXT, Markdown, HTML, e-books (and Pandoc's formats) → PDF** with Filee's own layout engine; files with what it doesn't draw yet keep their usual route (see [Filee renderer: PDF output](#filee-renderer-pdf-output)) |
| rhwp | bundled with the installer (`engines/rhwp`, with the Microsoft C++ runtime, see below) | HWP/HWPX → PDF, **HWPX → HWP** (HWP → HWPX too, ranked below the built-in HWP reader) |
| Archives (7-Zip) | bundled with the installer (`engines/7zip`, ~2.5 MB) + built-in readers | ZIP (including ANSYS Workbench WBPZ), 7Z, RAR, TAR (+ GZ/BZ2/XZ/Z/7Z/LZ), CAB, ISO, DMG, split (.001), … **→ folder, ZIP, 7Z, TAR, TAR.GZ/BZ2/XZ**; ALZ, EGG, lzip read in-process; "Compress into one archive" for any files |
| LibreOffice + H2Orestart + Java | **optional download** (~420 MB, 1.3 GB on disk) | older and rare formats only: DOC, XLS, PPT, RTF and OpenDocument → PDF and to each other, output as DOCX/ODT/ODS/ODP; HWP/HWPX → DOCX/ODT; read-only formats (see below) → PDF and their kind's editable formats; FODT, OpenDocument templates (OTT, OTS, OTP) and XLT out; SVG → EMF/WMF/ODG, a PDF page → SVG |
| Pandoc | **optional download** (~42 MB, 240 MB on disk) | Markdown ↔ DOCX/ODT/RTF, DOCX/ODT/HTML/RTF → Markdown, HTML ↔ DOCX/ODT, markup languages (reStructuredText, LaTeX, Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter, Textile, man, OPML) ↔ Markdown/HTML/DOCX/ODT and each other (and → RTF, EPUB, TXT), Markdown and markup → PPTX slides |
| Ghostscript | **optional download** (~20 MB, 31 MB on disk) | EPS/PS → PDF, PDF → EPS/PS, PostScript-based AI → PDF (images through PDF and PDFium); smaller PDFs when a preset asks for it |
| Tesseract | **optional download** (~50 MB, 160 MB on disk; each language 0.4–10 MB when first used) | **text recognition (OCR)**: scans and pictures → searchable PDF, and their text into DOCX, TXT, Markdown and HWPX ("Recognize text" in the preset, see below) |
| Apple Vision | part of macOS (Mac only) | **text recognition (OCR) on a Mac**, the same step as Tesseract without a download; Tesseract reads the languages Vision doesn't (see "Apple Vision on macOS") |
| FFmpeg | **optional download** (~100 MB, 272 MB on disk) | **all video and audio**: video ↔ video (H.264, HEVC, AV1), video → animated GIF / APNG, a still frame or every frame, audio and subtitle extraction, audio ↔ audio (also APE, WavPack, TTA, DSD, DTS, ALAC, …), GIF, APNG and photos → MP4/WEBM/MOV, Live / Motion Photos → their video, a clip of a video → an iPhone Live Photo or Android Motion Photo (experimental), images → HEIC, clips or audio files joined into one, pictures → a slideshow MP4, copies without metadata (see "Media tools") |
| Calibre | **optional download** (~216 MB, 660 MB on disk) | rare e-book formats: LIT, LRF, CHM, PDB, PML, RB, SNB, TCR, OEB → EPUB; EPUB → MOBI, AZW3, LIT, LRF, PDB, PML, RB, SNB, TCR; e-books → PDF (ranked above the HWPX writer, see below) |
| Whisper | **optional download** (Windows only; ~157 MB with model, ~160 MB on disk) | **Speech-to-text transcription**: video and audio → SRT subtitles in ~100 languages using OpenAI Whisper (whisper.cpp); runs locally and offline; needs FFmpeg |

DOCX, ODT, XLSX, XLS, ODS and PPTX → PDF need neither Microsoft Office nor LibreOffice: they are read in-process.
Documents and slides are written as HWPX and rendered by rhwp; spreadsheets are printed by Filee's own print engine
(Excel's page logic, see [Spreadsheet printing](#spreadsheet-printing)). Anything the built-in readers understand
(including PDF) is written as DOCX by the DOCX writer and as ODT by the ODT writer.
LibreOffice's and Pandoc's costs are set so the built-in route always wins where one exists.

Except for text that needs shaping. rhwp draws one glyph per character from left to right without OpenType shaping,
so Arabic and Hebrew come out unjoined and in the wrong order, and Indic vowel signs and conjuncts as separate glyphs
with dotted circles (#185 Gujarati, #202 Arabic). Its PDF edges are marked `Unshaped`, and rhwp tells
(`ITextShapingCheck`) whether a source has such text: `Infrastructure/ComplexScripts.cs` scans the text parts of the
file without reading it into the document model (DOCX `word/document.xml`, headers, footers and notes, PPTX slides,
XLSX shared strings and sheets, HWPX sections, OFD pages and templates, EPUB chapters, or a text file with its HTML /
RTF character references) for the Hebrew, Arabic, Syriac, Thaana, N'Ko, Indic, Sinhala, Thai, Lao, Tibetan, Myanmar,
Khmer, Mongolian and other South / Southeast Asian blocks. For such a file `JobQueue` plans again without unshaped
edges: DOCX, TXT, Markdown, HTML, PPTX and HWPX → PDF (and → images through PDF) go through LibreOffice even
when Settings → *Engines* doesn't choose it for documents (XLSX, XLS and ODS don't: the spreadsheet print engine
shapes with HarfBuzz). Without LibreOffice the file fails with "This script needs
LibreOffice to convert correctly" and the script's name instead of becoming a broken PDF. The same scan also finds
text rhwp has no widths for: its width tables cover only ASCII and Latin-1, general punctuation, math, the CJK forms
and Hangul (rhwp#8143), so a letter of any other script — Polish, Turkish, Vietnamese, Greek, Cyrillic, ... — or a
combining mark is drawn but laid out at a fixed half-em. Such a document takes a route without unshaped edges when
one exists and is never refused when none does: the overlapping text stays readable. Only letters inside words
count (an extended Latin letter next to any letter, a letter of another script next to one of its own script), so a
symbol or unit on its own (α, Ω, μm in a Korean report) keeps rhwp, and an HWPX stays with rhwp for this (only text
that needs shaping sends it elsewhere). Documents in French, German or Spanish (Latin-1 letters only), Chinese,
Japanese and Korean keep the built-in route; a binary HWP is not scanned and stays with rhwp.

All built-in document readers are registered in one place, `Hwp/Hwpx/DocumentReaders.cs` (format id → reader, and
which formats need Pandoc). The HWPX, DOCX and ODT writers take their input formats from it: adding a reader there is
one line and makes every writer (and every route through them) accept the format.

Every engine is built in, bundled with the installer or downloaded into Filee's own folder, so a conversion behaves
the same on every PC. An external engine is looked for in this order:

1. a copy the user picked in Settings → *Engines*;
2. Filee's own copy (bundled or downloaded);
3. a copy already on the computer: on `PATH`, in Homebrew's and the usual Unix folders, or where the LibreOffice,
   calibre, Ghostscript and Tesseract installers put it. Settings → *Engines* shows it as "Found on this PC", with a note when
   its version is older than the one Filee downloads.

Where Filee has no download of an engine (FFmpeg, Ghostscript and Tesseract on macOS, LibreOffice, Ghostscript and
Tesseract on Linux),
step 3 is how it is found. Microsoft Office
and Hancom Office are never used, even when installed. Running from source, the repository's `engines/` folder
(filled by `build/fetch-engines.ps1`) is used.

## Optional engines

The installer contains only the small engines. On first start Filee asks which of the large ones to download;
they can be installed or removed later in Settings → *Engines*. A conversion that needs a missing engine shows
"Needs LibreOffice …" on its donut slice.

- Downloads are pinned in `src/Filee.Engines/Infrastructure/engines.json` (URL, SHA-256, size). The same file is
  read by `build/fetch-engines.ps1`, so development and the app always use the same versions. A macOS download can
  name the oldest macOS it runs on (`minimumMacOS`); an older Mac isn't offered it.
- `EngineInstaller` verifies the SHA-256 before unpacking and unpacks into a staging folder that replaces the
  engine folder only when complete. Redirects to mirrors (even plain HTTP) are followed, because the hash decides.
- Engines go to `%LOCALAPPDATA%\Filee\engines`: outside the app folder, so updates keep them, and inside Filee's
  install root, so uninstalling removes them.
- LibreOffice and calibre come as MSIs and are unpacked with an administrative install (`msiexec /a`): files only,
  no registry entries, no admin rights. LibreOffice's help, gallery and most dictionaries are removed afterwards
  (~500 MB). msiexec gets the real paths of the package, the target and its log: the Windows Installer service won't
  follow a junction a user made (error 1620 or 1622, #527), as when `AppData\Local` was moved to another drive.
- Ghostscript comes from conda-forge (Artifex publishes only an NSIS installer): a `.conda` package is a zip with a
  zstd tarball, of which only `Library/bin` is unpacked (SharpCompress; `fetch-engines.ps1` uses Windows' `tar.exe`).
  Its fonts and resources are compiled into `gsdll64.dll`. The Microsoft C++ runtime it was built against
  (`vcruntime` component) is copied next to `gswin64c.exe`, so it runs on PCs without the VC++ Redistributable.
- Tesseract is UB Mannheim's Windows build (the one Tesseract's documentation points to), published only as an NSIS
  installer. It is never run: Filee's own 7-Zip unpacks it (`nsis` kind), and the training programs, manuals,
  installer files and bundled language data are removed (`TrimTesseract`, ~80 MB). The languages come from
  `tessdata_fast` 4.1.0, pinned like the engines in `Infrastructure/ocr-languages.json` (`traineddata` kind, one
  folder per language under `engines/ocr-languages`), and are downloaded the first time a preset uses one.

### Microsoft C++ runtime (Windows)

Programs built with Visual C++ import `MSVCP140.dll` / `VCRUNTIME140.dll`, which a fresh Windows does not have (they
come with the Visual C++ Redistributable). Windows looks for a program's DLLs in its own folder first, so Filee puts
the runtime next to every program that needs it and does not ship it (`Infrastructure/AppLocalRuntime.cs`):

| Binary | Runtime |
|---|---|
| `engines/rhwp/rhwp.exe` (bundled) | `vcruntime` component copied next to it by `fetch-engines.ps1` (#248) |
| `unhwp.dll` (NuGet, loaded into Filee.exe and filee-cli.exe) | `vcruntime` component copied next to `Filee.exe` (`build-installer.ps1` → `fetch-engines.ps1 -RuntimeTo`) |
| Ghostscript (download) | `vcruntime` component copied next to `gswin64c.exe` (`EngineInstaller`, `fetch-engines.ps1`) |
| LibreOffice (download) | its own copy, which the administrative install leaves in `System64`, copied into `program/` (`EngineInstaller`, `fetch-engines.ps1`; `LibreOfficeConverter` also repairs a copy installed by an earlier Filee) |
| calibre, Java (downloads) | ship their own next to their programs (`app/bin`, `bin`) |
| 7-Zip, FFmpeg (GyanD), Tesseract (UB Mannheim, MinGW), Pandoc, PDFium, SkiaSharp / HarfBuzz, Magick.Native, the Explorer menu DLL, .NET | import neither (static runtime or MinGW / GHC builds) |

The installer and the portable zip are made from the same publish folder, so both have the DLLs. Filee installed in
Program Files cannot repair its own bundled files, so an older installation gets them by installing the new version.
When a DLL is still missing, Windows does not start the program and reports `0xC0000135` (STATUS_DLL_NOT_FOUND;
`0xC0000139` for an outdated one) as its exit code: `ProcessRunner` and `FfmpegRunner` turn that into
`error.runtime_missing` ("A Microsoft Visual C++ runtime file this engine needs is missing or outdated …", with the
program and the code in brackets) instead of the raw exit code, and `UnhwpConverter` does the same when `unhwp.dll`
cannot be loaded. To check binaries after an engine update, list the DLLs each `.exe` / `.dll` imports (its PE import
table, e.g. `dumpbin /dependents`) and look for `MSVCP140*.dll`, `VCRUNTIME140*.dll` or `CONCRT140.dll`.

To bump a version, change `url`, `sha256` and `size` in engines.json together, run
`pwsh build/fetch-engines.ps1`, test, and update `THIRD-PARTY-NOTICES.md`. For rhwp, also update `RHWP_VERSION`,
`RHWP_COMMIT` and `RUST_TOOLCHAIN` in `build/build-native.py` (see below); CI fails until they match. The Windows
ARM64 builds of rhwp and Unhwp need a new build and pin too (see [Windows on ARM64](#windows-on-arm64)). When the
LibreOffice team retires a version from `download.documentfoundation.org/libreoffice/stable/`, its URL stops working:
keep the pinned version current.

## Linux builds for older glibc

Linux packages run on Ubuntu 22.04 (glibc 2.35). rhwp's Linux release is linked on Ubuntu 24.04 (needs glibc 2.39).
The `linux-native` job of `.github/workflows/portable.yml` builds it from its pinned, unmodified source on Ubuntu 22.04
(x64 and arm64) with `build/build-native.py`:

- `rhwp`: `cargo build --release --locked --bin rhwp` at the tag's commit with its pinned Rust toolchain, in the
  layout of rhwp's release archive.
- `unhwp` (arm64 only): Unhwp's NuGet package ships no linux-arm64 library, so `libunhwp.so` is built from the commit
  the package's nuspec names (`cargo build --release --locked -p unhwp --lib --features ffi`, as upstream builds it).
- `check` (also run on its own): fails when engines.json pins another rhwp version, or Directory.Packages.props
  another Unhwp version, than the script builds.

The Linux package jobs pass the result to `build/build-portable.ps1 -RhwpOverride <dir>` (arm64 also
`-UnhwpOverride <libunhwp.so>`, which the arm64 tests are built with too: `-p:UnhwpNativeLibrary`). Local builds without it use
the downloaded rhwp (fine on the machine that builds it). SharpHook's native libraries are packaged as they come
from NuGet: SharpHook 8.0.0 built its X11 back ends against glibc 2.38, 8.0.1 needs glibc 2.34 at most again.

`build/check-elf-glibc.py` lists the newest GLIBC version every ELF file in the package needs and fails above 2.35,
and the packages are unpacked and started on Ubuntu 22.04 (`build/smoke-portable.ps1`: CLI, rhwp conversions, loading
the X11 input hook back end; `build/smoke-launch-app.py`: the app under Xvfb). If a SharpHook or engine update fails
these checks, keep the previous version until upstream builds for an older glibc again, or build the binary from its
pinned source in `build/build-native.py` like rhwp (PR #113 did this for SharpHook 8.0.0's libuiohook back ends).

## Windows on ARM64

`build/build-installer.ps1 -Runtime win-arm64` builds `Filee-<version>-win-arm64-Setup.exe` (and `-Portable.zip`): the
app, the command line and Setup for win-arm64, the Explorer menu DLL for ARM64 (`build-explorer-menu.ps1
-Architecture arm64`, packaged as `arm64`), and ARM64 builds of the bundled engines (`fetch-engines.ps1 -Runtime
win-arm64`). The engines the app downloads stay x64: on Windows 11 on ARM, which runs them under emulation, the app
offers the win-x64 downloads (`EngineDownloads.DownloadRuntime`); Windows 10 on ARM can't run them and gets none.
Setup's engine list is made when it is packed (`build/tools/make-setup.cs`), so the ARM64 Setup offers the x64 one.

- **7-Zip**: the official ARM64 installer of the version engines.json pins, a 7z self-extracting archive unpacked by
  the x64 7-Zip (Windows' `tar.exe` can't undo its ARM64 executable filter).
- **rhwp** and **Unhwp's library** have no ARM64 release. `.github/workflows/windows-arm64-native.yml` (run it from the
  Actions tab once per version) builds them on a Windows ARM64 runner with `build/build-native.py`, from the same
  pinned commits as above, with the C runtime linked statically (`+crt-static`): they import no Visual C++ runtime DLL,
  so the ARM64 build ships none. Its artifact is hosted on dl.filee.sh and pinned by SHA-256 in `$arm64Pins` of
  `build/fetch-engines.ps1`; until a pin is filled in, a win-arm64 build stops and says so. `unhwp.dll` goes next to
  `Filee.exe` through the build (`-p:UnhwpNativeLibrary`).

`UnhwpConverter` reports Unhwp as available where its library loads, so a build without it shows the engine as not
available on this platform. CI's `build (win-arm64)` job builds and tests on Windows 11 ARM64; once both pins are filled
in it also builds the package (artifact `windows-arm64-package`).

## Images and vector graphics

- **Trim transparent borders** (preset *Trim transparent borders*, `ImageOptions.TrimTransparent`, #332) crops a PNG,
  WebP, … to the box around its pixels that aren't fully transparent (`ImageEncoder.TrimTransparentBorders`) and saves
  `name_trimmed` in the same format; the source is never overwritten. Only alpha counts: ImageMagick's own trim
  takes the corners' colour, so content touching a corner would lose a solid border. Pictures without transparent
  borders, fully transparent ones and multi-frame animations (each frame alone would shift) come out as they are.
  Formats that can't be transparent (JPG, HEIC, …) have nothing to trim: the preset is greyed out for them, and a
  JPG in a mixed selection fails with that reason instead of being re-encoded.
- **Animations.** An animated GIF, WebP, APNG or MNG keeps every frame and its timing when converted to one of these
  (animated GIF ↔ animated WebP ↔ APNG ↔ MNG, WebP lossy or lossless as the preset says) and becomes a multi-page TIFF;
  other targets get the first frame. Multi-page TIFFs and PDFs stay one image per page in WebP.
- **Playback speed** (preset option *Playback speed*, `ImageOptions.AnimationSpeed`: 0.25×, 0.5×, 0.75×, 1×, 1.5×,
  2×, 4×; shown for GIF, WebP, APNG, MNG and "same as source") rewrites the frame delays of an animation that stays
  animated (`Magick/AnimationTiming.cs`). Delays are divided by the speed in the target's own units (hundredths of a
  second for GIF and MNG, milliseconds for WebP and APNG) and rounded on the running total, so the whole animation
  keeps its new length. Players show a frame of 10 ms or less for 100 ms, so no delay goes below 20 ms (50 frames a
  second): a frame that would start sooner than that after the one before is dropped and its time goes to that one,
  which leaves frames out evenly. Source delays of 10 ms or less count as the 100 ms players show. Loop count and
  disposal stay; 1× (also every preset saved before the option) leaves the timing untouched. Video → GIF / APNG
  (FFmpeg) puts `setpts=PTS/<speed>` before the `fps` filter, so the frame rate limits stay. The speed changes only in
  the step that writes the output (`JobQueue`), never twice on a longer route.
- **APNG** (`Magick/Apng.cs`) is Filee's own code, written from the APNG specification: ImageMagick reads only an
  APNG's default image and writes APNG through an external FFmpeg. Every frame is an ordinary PNG decoded or encoded by
  ImageMagick; Filee handles the chunks around them.
  - Reading composes each frame onto the canvas as a player shows it: `fcTL` regions and offsets, blend `SOURCE` / `OVER`,
    dispose `NONE` / `BACKGROUND` / `PREVIOUS`, delays (a denominator of 0 means hundredths) and the loop count. A
    default image that comes before the first `fcTL` is not part of the animation and is skipped. Animated PNGs saved
    as `.png` are recognised by their `acTL` chunk and stay animated as GIF, WebP or APNG; other targets get the first
    frame.
  - Writing gives every frame the same pixel format (8-bit RGB, or RGBA when any frame is transparent or smaller than
    the canvas): the first frame is the default image (`IDAT`, so viewers without APNG support show it), the others are
    `fdAT` chunks holding only the rectangle that changed (dispose `NONE`, blend `SOURCE`); a frame identical to the one
    before lengthens its delay instead. Checked with FFmpeg's APNG demuxer.
  - APNG ↔ video needs FFmpeg: video → APNG uses its `apng` encoder and muxer (24-bit RGB, looping, the GIF's frame rate
    and width limits), APNG → MP4/WEBM/MOV its APNG demuxer. Animated WebP and GIF still reach video through GIF, a
    still picture through PNG.
- **More raster formats** through ImageMagick's own coders, each checked against `MagickNET.SupportedFormats` and
  converted there and back in `ImageFormatTests`: QOI, MNG (animated), DDS (DXT block compression, which is lossy; of
  a texture with mipmaps the largest image is read; uncompressed 32-bit pixels stored R, G, B, A, which ImageMagick
  reads with red and blue swapped, are swapped back when converting and in the crop tool), PCX, CUR (at most 256 × 256, larger pictures are scaled down), XPM,
  XBM and WBMP (1-bit), PAM, PFM, SGI (`.sgi`, `.rgb`, `.rgba`, `.bw`, always read as SGI; ImageMagick would take
  `.rgb` for headerless pixels), Sun Raster (`.ras`, `.sun`) and MIFF. XBM, WBMP, PFM, PCX and Sun Raster are written
  without transparency (flattened onto the preset's background): PFM has none, and the alpha planes ImageMagick can
  put into PCX and Sun Raster are read by hardly any other program. Not offered: PICT, because ImageMagick can't read
  back direct-colour PICTs wider than about 130 pixels that it wrote itself.
- **Read only**: Photo CD (the 768 × 512 base image), OpenEXR and Radiance HDR. The bundled Magick.NET is the 8-bit
  (Q8) build without HDRI, so high-dynamic-range values are clipped to the displayable range and stored with 8 bits per
  channel; there is no tone mapping. Writing EXR or HDR from 8-bit pictures would only make larger files, so they are
  not targets.
- **JPEG XR** (`.jxr`, `.wdp`, `.hdp`, read only, Windows only; `Wic/`). ImageMagick has no JPEG XR coder in any build,
  so the picture is decoded by Windows' own codec (WIC's JPEG XR decoder, in every Windows 10 and 11) and handed to
  ImageMagick for the preset's options and the output format. Elsewhere the engine reports "not available on this
  platform". 8- and 16-bit pictures are converted to 8-bit sRGB by WIC. Fixed-point and float ones are linear scRGB
  (1.0 = SDR white at 80 nits), like the 64-bit half-float screenshots Windows saves on an HDR display (Xbox Game Bar,
  Win + Alt + PrtScn, Snipping Tool), and are tone-mapped (`Wic/HdrToneMap.cs`) instead of clipped:
  - the reference white is the luminance 99 % of the pixels stay below, between 1.0 and 4.0 (80–320 nits). Windows
    draws SDR content at the display's "SDR content brightness", so the white of a desktop screenshot lands there and
    its SDR parts come out as they were drawn; small highlights don't darken the picture;
  - values up to 80 % of that white are kept; above, an extended Reinhard shoulder takes everything up to the brightest
    pixel into the last 20 %, on the largest of R, G and B so colours keep their hue. A picture with nothing brighter
    than white is left as it is;
  - colours outside the sRGB gamut are clipped, then the sRGB transfer curve and 8 bits. An alpha channel that is
    opaque everywhere (HDR screenshots have one) is dropped.
- **DICOM** (`.dcm`, `.dicom`, read only). The Q8 build reads only 8-bit DICOM, while CT, MR and X-ray scanners store
  12 or 16 bits, so `Magick/Dicom.cs` (written from the DICOM standard, PS3.5 and PS3.3 C.7.6.3 / C.11.2) reads
  uncompressed files itself: implicit and explicit VR little endian, explicit VR big endian and deflated files, with or
  without the 128-byte preamble; 8 or 16 bits allocated, signed or unsigned; MONOCHROME1 (inverted) and MONOCHROME2,
  RGB and YBR_FULL (interleaved or planar); every frame of a multi-frame file. Grey values go through the rescale
  slope / intercept and the file's first window (centre / width) like a viewer's default view, or are stretched over
  the file's range when there is no window, to 8 bits. Compressed pixel data (JPEG, JPEG-LS, JPEG 2000, RLE) is left to
  ImageMagick, which manages only 8-bit images; others fail with a message asking for an uncompressed export.
- **FITS** (`.fits`, `.fts`, read only; `.fit` is left to Garmin's FIT). Astronomy pictures from telescope cameras,
  capture and stacking programs. `Magick/Fits.cs` (written from the FITS 4.0 standard) reads the first HDU with a
  picture, the primary one or the first IMAGE extension: 2880-byte blocks of 80-character cards, big-endian data,
  BITPIX 8, 16, 32, 64, -32 and -64, physical values BZERO + BSCALE × stored (BLANK integers become undefined), a
  cube of three planes as red, green and blue (other cubes give their first plane). The Bayer pattern comes from
  `BAYERPAT` or `COLORTYP` (RGGB, BGGR, GRBG, GBRG) at the first stored pixel, shifted by `XBAYROFF` / `YBAYROFF`.
  `ROWORDER = 'BOTTOM-UP'` turns the picture upright; without it rows are taken top-down, as capture programs write
  them. Tile-compressed files (fpack, a BINTABLE with `ZIMAGE = T`) fail with a message asking to `funpack` them.
  - **PNG, JPG and every other target** (`FitsRendering`): stretched automatically like a capture program's preview.
    The values are scaled to 0–1 over their range; per channel, the shadows are clipped 2.8 normalized MADs
    (1.4826 × the median absolute deviation) below the median and a midtones transfer function takes the median to
    0.2. A colour camera's mosaic is debayered first (bilinear: each missing colour is the average of the neighbours of
    that colour) unless the preset's *Debayer FITS from a colour camera* is off (`ImageOptions.FitsDebayer`); its
    channels are stretched one by one, so the sky comes out neutral. 8 bits per channel, then ImageMagick as usual.
  - **TIFF** (`TiffWriter`, as the Q8 build stores 8 bits): *Linear* (the data as it is, the default) or *Stretched*
    (`ImageOptions.FitsTiff`), at the file's own depth: 8-bit and 16-bit data stay so when they are whole numbers in
    range (unsigned 16-bit through BZERO 32768 included); 32-bit integers, floating point and signed or scaled values
    that don't fit become 32-bit floating point. Never debayered, so another program can still debayer the mosaic.
    Uncompressed, LZW or Deflate as the preset's TIFF compression says (integers with the horizontal predictor);
    turning and mirroring apply, resizing and the other picture options don't. FITS → TIFF costs a little more than
    other edges, so other formats are reached through a stretched picture, never through the linear TIFF.
- **Fujifilm HEIF** (`.hif`) is read as HEIC.
- **Rotate and flip** happen after the EXIF orientation is applied, in the one step of a route that turns images
  (`IImageTransformer`). There is no lossless JPEG rotation: a turned JPEG is re-encoded, at its own estimated quality
  when that is lower than the preset's, so a phone photo saved at 75 doesn't come back bigger at 95.
- **Layered files.** PSD/PSB convert from Photoshop's composite (ImageMagick merges the layers when a file was saved
  without "maximize compatibility"). GIMP XCF layers are composited at their offsets with their blend modes; hidden
  layers are left out. ImageMagick reads 8-bit XCF without zlib tile compression (GIMP 2.10+ "better but slower
  compression" files give a clear error).
- **LIVP** (an iPhone Live Photo as iCloud downloads it: a ZIP of the photo, HEIC or JPEG, and its MOV) is read as its
  photo, taken out of the package in memory (`LivePhotoSource.ReadStill`, at most 64 MB); its video is FFmpeg's (see
  Live / Motion Photos there).
- **Live / Motion Photos from a video** (JPG and HEIC targets) are FFmpeg's: the clip and the photo's frame come from
  it, ImageMagick or the HEIC writer only encode that frame (see *Making Live / Motion Photos* there).
- **Camera RAW** (DNG, CR2, CR3, NEF, ARW, …, and `.raw`) is decoded by LibRaw with the camera's white balance,
  sRGB output and AHD demosaicing, then turned upright from the EXIF orientation.
- **EMF/WMF** are drawn by Filee's own [metafile renderer](#emf--wmf-renderer) on every platform: images at the
  preset's render DPI (at least 150), keeping their physical size, and PDF as vector drawing.
- **SVG → PDF** replays the drawing into Skia's PDF backend: paths, gradients and clips stay vector. Text is written
  as glyph outlines (shaped with HarfBuzz), because SkiaSharp's PDF backend has no font subsetter and would embed
  every font whole (13 MB for one line of Korean). SVG → images is rendered at its final size: the preset's DPI, at
  least 1024 px on the long edge, or exactly the preset's resize target. External files next to the SVG are
  loaded; nothing is fetched from the network and scripts don't run.
- **ICNS** files are written with every size iconutil makes (16–1024 px, PNG), the picture centred on a transparent
  square; reading takes the largest image (PNG, JPEG 2000 or the old RLE + mask entries).
- **Illustrator** files saved with "Create PDF compatible file" (the default since Illustrator 9) are PDFs and need
  nothing extra; PostScript-based ones need Ghostscript.
- EPS and PS → PNG go straight through Ghostscript's `pngalpha` device, so the background stays transparent;
  JPG, WebP, … and AI reach images through PDF (Ghostscript → PDFium), and SVG reaches EPS through PDF.

## EMF / WMF renderer

Built in (`Metafiles/`), the same on Windows, Linux and macOS: Filee's own interpreter of Windows metafiles, written
from Microsoft's [MS-EMF], [MS-WMF] and [MS-EMFPLUS] specifications, drawing to an `SKCanvas` (no GDI+, LibreOffice or
ImageMagick needed). `EmfPlayer` and `WmfPlayer` read the records, `GdiPlayer` keeps GDI's device context and draws,
and the drawing is recorded into an `SKPicture` that is replayed for each output:

- **EMF / WMF → PNG, JPG, …** (image engine, `Magick/ImageReader`): rendered at the preset's render DPI (at least 150),
  on a transparent background, with the DPI recorded so the picture keeps its physical size. An EMF+ only file (see
  below) is drawn by GDI+ through ImageMagick on Windows, as before; elsewhere it fails with that reason.
- **EMF / WMF → PDF** (vector engine): a page the size of the picture frame with the drawing as vector graphics (paths,
  text as glyph outlines, bitmaps as images). PDFsharp's picture-to-PDF route is no longer offered for metafiles.
- **Pictures in documents** (layout engine, `Typesetting/FloatingObjects` and table cell fills): an EMF or WMF picture
  (clip art and OLE previews in HWP, Word's EMF pictures) is replayed into the page, so the PDF keeps it as vector
  drawing; its natural size is its frame (`Documents/PictureSize`). A picture that can't be drawn is left out, like a
  picture of any format that can't be read.
- Pandoc's Markdown / HTML results get PNGs of their EMF / WMF pictures on every platform.

What is drawn:

- **Coordinates**: all map modes, window / viewport origins and extents (scaled, offset), world transforms
  (`SetWorldTransform`, `ModifyWorldTransform`), SaveDC / RestoreDC. An EMF's frame (0.01 mm) is the picture; a WMF's
  placeable header box is, played as GDI+ plays it (MM_ANISOTROPIC with window and viewport on the box), and a WMF
  without that header takes its box from its first window records.
- **Lines and shapes**: lines, polylines, polygons and poly-polygons (alternate / winding), Bézier curves, PolyDraw,
  rectangles, rounded rectangles, ellipses, arcs, chords, pies, ArcTo and AngleArc (arc direction followed), pixels.
- **Pens**: cosmetic and geometric, solid, dash, dot, dash-dot, dash-dot-dot, alternate and user styles, end caps,
  joins and the miter limit, PS_INSIDEFRAME, the background colour in the gaps of dashed pens (OPAQUE mode). A pen made
  by CreatePen is one pixel wide only at width 0; its dash styles hold only while it is a pixel wide, as in GDI.
- **Brushes**: solid, hatched (8 × 8 pixels, with the background colour in OPAQUE mode), DIB pattern and monochrome
  pattern brushes (text and background colours), the brush origin.
- **Paths**: BeginPath … EndPath with every drawing call above and text, CloseFigure, FillPath, StrokePath,
  StrokeAndFillPath, WidenPath, SelectClipPath, AbortPath.
- **Clipping**: IntersectClipRect, ExcludeClipRect, OffsetClipRgn, ExtSelectClipRgn (all region modes), clip paths,
  the meta region; regions are filled, framed, painted and inverted.
- **Text**: ExtTextOut (A and W), PolyTextOut, SmallTextOut, WMF TextOut / ExtTextOut. The LOGFONT's face name goes
  through the layout engine's font resolver (other names, a font of the same script when it is missing, fallback per
  character) and the text is shaped with HarfBuzz; where the record gives advances (the dx array, and ETO_PDY's
  vertical ones) each character's cluster starts where they put it. Alignment (left / centre / right, top / baseline /
  bottom, TA_UPDATECP), escapement, width (LOGFONT lfWidth), bold and italic (synthesized where the family lacks them),
  underline, strike-out, ETO_OPAQUE / ETO_CLIPPED, opaque text cells, ANSI text in the font's code page (CP949, Shift
  JIS, GBK, Big5, …), symbol fonts. Glyph-index text (ETO_GLYPH_INDEX) and symbol-font text are drawn only with the
  named font itself (glyph numbers of another font would be the wrong signs). For pixels, upright text is hinted at
  whole-pixel sizes and positions as GDI draws it; elsewhere text is drawn as outlines (in PDFs too, as SVG text is,
  since SkiaSharp's PDF backend would embed fonts whole).
- **Bitmaps**: BitBlt, StretchBlt, StretchDIBits, SetDIBitsToDevice (partial scan lines too), AlphaBlend (constant and
  per-pixel alpha), TransparentBlt, MaskBlt, PlgBlt, WMF DIB and Bitmap16 blits, pattern blits (PatBlt and blits
  without a source). DIBs of 1, 4, 8, 16, 24 and 32 bits per pixel, RLE4 / RLE8, bit fields, JPEG and PNG pixels;
  palette-index colour tables. GradientFill rectangles and triangles (triangles as small flat pieces, which the PDF
  backend keeps).
- **Raster operations**: SetROP2 and the blits' ternary operations map to blend modes (AND → multiply, OR → screen,
  XOR → difference, NOT → inverted), exact for black and white, which is what masks use; PSDPxax / DSPDxax use the
  source as a mask for the brush.
- **EMF+**: an "EMF+ dual" file (GDI+'s default) carries its drawing twice; the EMF records are drawn and the EMF+
  records skipped. An "EMF+ only" file has no EMF drawing to fall back on: it is refused (NotSupportedException), and
  on Windows the image engine hands it to GDI+.
- Not drawn: flood fills (they need the pixels already drawn), OpenGL records, colour management, right-to-left layout
  (EMR_SETLAYOUT), escapes.

Where GDI's rasterization shows, a bitmap follows it: one-pixel lines are aliased and on GDI's pixels, fills are not
smoothed, pen widths are whole pixels, a rectangle or ellipse leaves out its right and bottom pixel row. Bitmaps are
smoothed when scaled, as GDI+ draws them. Vector output (PDF, the layout engine's pages) has none of this.

Hostile files (`Metafiles/MetafileLimits.cs`): at most 512 MB per file, 5,000,000 records, 65,535 object table entries,
1,024 saved device contexts, 4,000,000 path points, 100,000 rectangles per region, 8,192 × 8,192 pixels per bitmap and
256 M pixels of bitmaps per file; past a limit the file is refused with `error.document_too_complex`. Damaged files (a
record longer than the file or shorter than a record, a count its record can't hold, an object outside the table, a
bitmap whose pixels are cut off) stop with an InvalidDataException naming the record, its type and where it starts.
Every record moves on by at least 8 bytes (EMF) or 6 (WMF), so there are no loops; bitmap sizes are checked before
anything is allocated.

Tests: `MetafileTests` (every platform) writes EMF and WMF files record by record in code and checks the pixels, the
PNG and vector PDF conversions, the layout engine's pages, EMF+ dual / only and the hostile cases (cut off, oversized
and empty records, huge counts, wrong object numbers, the limits, random damage). `MetafileGdiTests` (Windows) records
metafiles with GDI and GDI+, draws them with GDI+ and with Filee at 4 × the size, and compares 400 × 300 pictures of
4 × 4 pixel blocks (GDI doesn't smooth edges and Filee does; the blocks measure where things are and what colour):

| Recorded with | SSIM (4 × 4 blocks) | SSIM (drawn at 400 × 300) |
|---|---|---|
| GDI: shapes, pens, hatch and solid brushes | 0.990 | 0.903 |
| GDI: bitmap, filled path, clip, world transform | 0.989 | 0.957 |
| GDI → WMF (GetWinMetaFileBits): shapes and text | 0.987 | 0.936 |
| GDI+, EMF only: shapes, hatch, gradient, dashed Bézier, rotation | 0.983 | 0.913 |
| GDI+, EMF+ dual: the same | 0.972 | 0.882 |
| GDI+, EMF only: bitmap, clip | 0.988 | 0.912 |
| GDI+, EMF only: text (Arial, Malgun Gothic bold, Times New Roman italic) | 0.967 | 0.637 |
| GDI: text (Arial, Times New Roman, Malgun Gothic, centred on a baseline) | 0.921 | 0.407 |

Text is held to 0.9: GDI hints glyphs to the pixel grid and draws their stems darker and wider than the outlines.
For EMF+ dual files GDI+ draws the EMF+ records, which lay text out with GDI+'s own grid-fitted advances and carry
undithered bitmaps, while the EMF records Filee draws (and GDI+ draws for an EMF-only file) don't: GDI+'s own pictures
of the two records agree at 0.59 for text and 0.90 for a bitmap, and Filee reaches 0.58 and 0.89 against the EMF+ one.

## Tracing (raster images → SVG)

Built in (`Vector/Tracing`), no Potrace or other program: Filee's own code, written from published descriptions of the
steps (no code of Potrace, AutoTrace or VTracer is used). Every raster format ImageMagick reads (PNG, JPG, BMP, GIF,
WEBP, TIFF, HEIC, PSD, RAW, DICOM, …) is traced, and other raster images through PNG (ICNS, APNG). Only raster images are traced:
PDF, EPS, PS, AI, EMF/WMF, CDR, documents and videos are never redrawn from a rendering, so without a real vector
route they have no SVG target (PDF → SVG, for example, needs LibreOffice). The first frame or page is used, turned upright, in sRGB. The SVG has the picture's pixel size
(`width`/`height`/`viewBox` in pixels), so it lines up with the original.

- **Black and white** (*Line art to SVG*: logos, signatures, scans): pixels darker than Otsu's threshold are ink
  (transparent pixels count as white paper). On a transparent background the opaque pixels are the ink, or only their
  darker part when the opaque area itself is light and dark. One black `<path>` on a transparent background.
- **Colour** (*Trace to SVG*: illustrations, icons): k-means reduces the picture to the preset's number of colours
  (2–64; run on the mean colours of 32 768 colour boxes weighted by their pixel counts, so it sees every pixel). One-pixel
  bands of in-between colours along edges (anti-aliasing) join the colour on either side; regions smaller than a few
  pixels (more on large pictures) join the neighbour they share most border with. Colours are stacked largest first,
  and each colour's shape is the union of itself and every colour above it: lower shapes run under upper ones, so no
  background shows through between neighbouring shapes. Transparent areas stay transparent. One `<path>` per colour.
- **Outlines**: each shape's boundary is followed along the pixel edges (inside always on the right, so holes come out
  reversed and fill correctly with the non-zero rule; pixels touching only at a corner stay one shape). Outlines
  smaller than the speck size are dropped (and small holes filled).
- **Curves**: the staircase is replaced by points on the edge it approximates (midpoints of the steps); corners are
  kept where two long pixel runs meet, at the ends of thin lines, where the direction turns sharply within two
  pixels, or where a gentler turn happens all at once (unlike a small circle). Points between corners are smoothed;
  stretches that stay within half a pixel of a line without bending become lines; the rest becomes cubic Béziers
  fitted by least squares (Philip J. Schneider, "An Algorithm for Automatically Fitting Digitized Curves", Graphics
  Gems, 1990), meeting lines and each other with matching tangents.
- **Options** (Settings → Presets): mode, colours (colour mode) and *Detail* 0–100. Low detail drops larger specks,
  smooths more and allows the curves 1.15 px of deviation; high detail keeps every pixel's shape (0.25 px). The
  default 50 drops specks under 6 px (black and white) or merges regions under 17 px (colour), at 0.7 px; speck and
  region sizes grow with pictures larger than 1000 px (up to four times).
- **Size and speed**: pictures over 6 megapixels or 4096 px on a side are reduced before tracing; the SVG keeps the
  original size and says so in a comment. A 2000 × 2000 illustration traces in 1–3 s at 16 colours and well under a
  second in black and white; a noisy 2000 × 2000 photo at 16 colours takes about 4 s (64 colours about 5 s, and a
  20 MB SVG). Photos are not what tracing is for: they come out as posterized patches.
- **Routes**: tracing is the only raster → SVG route and costs 40 (`VectorConverter.TraceCost`), more than any route
  from an image to a format SVG leads to, so it is never a step on the way to PDF, PNG, EPS, …; a test checks every
  pair of formats. Its edges are marked `ImageSourcesOnly`: the planner uses them only when the file being
  converted is a raster image (`FormatCategory.Image`), so a vector file rendered to PNG can't be traced; a test
  checks this for every pair of formats too. Through SVG, pictures reach DXF and DWG.

## SVG → DXF / DWG

`Vector/SvgDxfWriter.cs` writes the drawing's outlines with ACadSharp (DXF in the preset's version like the CAD engine; DWG is
DXF → DWG by the CAD engine). The SVG is loaded with Svg.Skia as for PDF output, so groups, transforms, `<use>`,
nested `<svg>`, CSS and text are resolved the same way.

- Circles and ellipses become CIRCLE and ELLIPSE (also rotated, scaled or skewed: the transformed axes come from the
  singular values of the transform). Paths, lines, polylines, polygons and rectangles (rounded too) become
  LWPOLYLINEs, closed where the shape is closed, or a LINE for one straight segment. Bézier curves and arcs are divided
  into straight pieces within 0.01 mm of the curve: every CAD and CAM program reads polylines, while SPLINE support
  varies. Text becomes its glyph outlines.
- Units: SVG user units are CSS pixels, 96 per inch, so 1 px = 25.4 / 96 mm and the DXF is in millimetres
  (`$INSUNITS` 4, metric): a drawing 100 mm wide in the SVG is 100 mm wide in CAD. The y axis is flipped (SVG points
  down, DXF up) with the viewport's bottom-left corner at (0, 0).
- Colours are kept as true colours (the stroke's, else the fill's; black for gradients). Fills, stroke widths,
  clipping, masks, images and filters are not represented; a shape both filled and stroked is written once. An SVG
  with nothing to outline fails with a message.

## HWPX writer

Filee writes OWPML (HWPX) itself, so saving as HWPX needs no Hancom Office and shows no approval dialog.
Readers turn the source into a small document model (`Hwp/Hwpx/HwpxModel.cs`) and `HwpxWriter` writes the package.
The model also keeps what a renderer needs and the writers ignore (`Hwp/Hwpx/HwpxModel.Layout.cs`): line rules with
the source's meaning (Word's multiple and document grid, 한글's "between lines"), 한글's seven language slots and Word's
four run fonts, paragraph borders and line breaking, stored line layouts, crops, rotation, z-order, master pages,
polygons, curves and arcs (`HwpxReader.ReadForLayout` also keeps runs apart by that detail and reads master pages and
those shapes, which `Read` leaves out as before). `StyleResolver` turns what a document leaves to the
한글 style into concrete values. What the readers take over:

- **DOCX** is read directly (`Hwp/Hwpx/Docx/DocxReader*.cs`) so the layout survives:
  - page size, orientation and margins per section; columns, also when they change mid-page;
  - headers and footers (odd/even pages, hidden on the first page) with page numbers and page counts;
  - paragraph formatting (alignment, indents, spacing, line spacing, tab stops, keep with next);
  - character formatting (fonts incl. theme fonts, size, colour, highlight and shading, bold/italic/underline/strike, super/subscript, spacing);
  - lists with Word's numbering formats (1., 가., ①, i., bullets);
  - symbols of Wingdings, Wingdings 2, Wingdings 3, Webdings and Symbol (Insert → Symbol, bullets, text typed in
    those fonts) as the Unicode characters they show: check boxes ☐ ☑ ☒, check marks and crosses, arrows, stars,
    hands, phone and mail signs, Greek letters and math signs (`Hwp/Hwpx/SymbolFonts.cs`, #350; PPTX bullets and
    text too). Codes without a table entry stay as before (an unknown bullet is "●");
  - tables with merged cells, column widths, borders and shading from the table style and cells, repeated header rows;
  - pictures inline and floating, text boxes, rectangles/ellipses/lines with solid or gradient fills, groups;
  - footnotes, endnotes, bookmarks, hyperlinks (web and within the document, e.g. a table of contents);
  - equations (Word's equation editor, Office Math) as 한글 equations, see *Equations* below;
  - Word's document grid ("lines"), which 한글 does not have, as an "at least" line spacing.
- **XLSX, XLS, ODS, CSV and TSV** (`Office/Sheets`, see [Spreadsheets](#spreadsheets)): every visible sheet becomes a section with one table. Cells show what Excel
  shows (number formats via ExcelNumberFormat, dates, percentages, cached formula results), with fonts, fills,
  borders, alignment and merged cells; an XLSX's rows to repeat at the top (Print Titles, #407), else frozen top
  rows, repeat on every page, and rows above the print titles come once before them. The page is A4 with narrow margins,
  landscape when the columns are much wider than portrait, and the table is scaled to the page width like
  Excel's "Fit all columns on one page". CSV is read with its delimiter (comma, semicolon, tab) and encoding
  (UTF-8 / UTF-16 with BOM; else CP949 when it looks Korean or on Korean Windows, as Korean Excel saves it; else the PC's code page). Not converted: charts, pictures, conditional
  formatting, print areas and page breaks.
- **PPTX** (`Hwp/Hwpx/Pptx`): one page per slide, sized like the slide, with every object floating at its place:
  placeholders inherit position and text style from the layout and master, text with bullets and numbering,
  shapes (rectangles, rounded rectangles, ellipses, lines) with solid or gradient fills and outlines, pictures,
  tables with PowerPoint's default table style, groups, master decorations and the slide background. Not
  converted: charts, SmartArt, rotation, effects (shadows, glow), picture cropping, animations, notes and hidden
  slides.
- **TXT** needs no reader (one paragraph per line; UTF-8, UTF-16, CP949 when the bytes look Korean or, on Korean
  Windows, are valid CP949, else the PC's code page: GBK on Chinese Windows, Shift-JIS on Japanese, Windows-1252 on
  Western, as subtitles are read; a `charset=` in the text isn't followed).
- **Markdown** is parsed with Markdig (`MarkdownReader.cs`): headings, emphasis, code, lists (1. / a. / i. with
  start numbers), task lists, quotes, tables with spans, links, images, footnotes and math. No Pandoc needed.
- **PDF** is read with PdfPig (see *PDF reader* below); DOCM / DOTX / DOTM, XLSM / XLTX and PPTM / POTX / POTM / PPSX
  are read like DOCX, XLSX and PPTX.
- **HTML** is parsed with AngleSharp (`HtmlReader.cs`, `HtmlCss.cs`): headings, paragraphs, bold / italic /
  underline / strike / sub / sup / code / mark, the basic inline CSS (colour, background, font weight, style and
  size, text-align, text-indent, page breaks) and simple `tag` / `.class` rules of style sheets, nested lists with
  start numbers and types, tables with spans / header rows / borders, pictures (local files and data: URIs; remote
  pictures are skipped), links and anchors, quotes, `pre`, `hr` and figures. Scripts, styles, forms and `nav` are
  skipped. The encoding comes from the BOM, the XML declaration or `<meta charset>`, else UTF-8 or the system
  code page. No Pandoc needed.
- **MHTML** (`.mhtml`, `.mht`) is turned into its standalone page first (see *E-mail* below) and that page read like
  HTML; it refers to nothing but data: URIs, so no file outside the job's scratch folder is looked at.
- **ODT** is read directly (`Hwp/Hwpx/Odt/OdtReader*.cs`, see *ODT reader and writer*), without Pandoc.
- **RTF** (`RtfReader.cs`, no Pandoc needed) as Word, WordPad, LibreOffice and 한글 write it: text in the code page
  of its font (`\fcharset`, `\cpg`) or the document's (`\ansicpg`, so Korean, Chinese and Japanese RTF reads right)
  and `\uN` characters; bold, italic, underline, strike-through, raised / lowered, size, font, colour and highlight;
  alignment, indents, spacing, line spacing and keep options; the style sheet (headings by `heading N` or
  `\outlinelevel`); lists from `\listtable` / `\listoverridetable`; tables (`\trowd` … `\row`, merged and shaded
  cells, borders as a grid or none, nested tables flattened into their cell); PNG, JPEG, EMF and WMF pictures sized
  by `\picwgoal`; footnotes and endnotes; hyperlink, page and page count fields; bookmarks; headers and footers
  (first-page ones are left out); sections with their paper, margins, orientation and columns. Unknown `\*`
  destinations, index entries, comments and object data are skipped; group depth, paragraphs and pictures count
  against the reader limits.
- Markdown, HTML and Pandoc keep structure only, so page setup comes from the built-in template (A4).

Element order and attribute values follow files saved by 한글 where the schema and 한글 disagree, for example:

- `hp:default` repeats paragraph margins and fixed line spacing doubled (`hp:case` holds the real values);
- page width/height are always those of the portrait sheet, `landscape="NARROWLY"` turns it;
- 한글 keeps a hanging paragraph's first line at `left` and indents the other lines;
- object offsets are unsigned: objects reaching into the margin are anchored to the paper instead;
- layout caches (table row heights, inline tab widths) are estimated, because simpler readers use them as-is.

Every generated file is checked in the tests with three independent readers: rhwp (renders it), Unhwp (reads the
text) and LibreOffice + H2Orestart (opens it), see `tests/Filee.Engines.Tests/DocxToHwpxTests.cs` and
`OfficeTests.cs`.

Not converted: Word charts, SmartArt, free-form shapes, tracked changes and comments.

### Equations

Word equations (`m:oMath`, `m:oMathPara`) become 한글 equation objects (`hp:equation`) with an equation script
(`Hwp/Hwpx/Docx/OmmlReader.cs` reads them into a small math tree, `Hwp/Hwpx/EquationScript.cs` writes the script),
so DOCX → HWPX and HWP keep editable equations and DOCX → PDF draws them with rhwp (#143):

- fractions (`{a} over {b}`, also without bar or written linear `a/b`), sub- and superscripts and scripts before the
  base, square roots and roots with an index, sums, products, unions and integrals with their limits, brackets that
  grow (`LEFT ( … RIGHT )`), matrices (`pmatrix`, `bmatrix`, `dmatrix`), cases, equation arrays (`pile`), function
  names and `lim` with what goes under it, accents (hat, bar, vec, dot, tilde …), over- and underlines;
- Greek letters, operators and arrows as 한글's commands (`omega`, `TIMES`, `LEQ`, `INF`, `rarrow` …), upright text
  (`m:sty="p"`, normal text) in `rm`, bold in `bold`; a display equation alone in its paragraph is centred;
- variables are written letter by letter when they read like a command (`x _{i n}`, not `in` = ∈);
- boxes, phantoms and group characters (braces above or below) keep only their content; other characters are
  written as they are.

The box an equation is stored with (`hp:sz`) is measured like rhwp lays the script out (estimated character
widths, script scale 0.7, fraction and root spacing, from rhwp's `equation/layout.rs`): rhwp stretches an equation to
its stored width, so a wrong box would distort it. 한글 lays the script out again when the equation is edited.
The model's other writers (HTML, EPUB, Markdown, TXT …) write a linear form instead, `(a+b)/(2c)`, `x_i^2`, `√(x)`,
which is also the equation's alternative text in HWPX.

## Spreadsheets

`SpreadsheetConverter` (engine id `spreadsheet`) converts between XLSX, XLS (and XLT templates, read only), ODS, CSV and TSV (also
`.tab`) in-process; every format goes through one in-memory workbook (`Office/Sheets/Workbook.cs`) whose cells keep
the text the spreadsheet shows, the value behind it and its Excel number format.

| Format | Read | Written |
|---|---|---|
| XLSX | own reader: values, number formats, rich text, fonts, fills, borders, alignment, merges, widths, heights, hidden rows / columns / sheets, frozen rows, print titles; for printing the page setup, print areas, page breaks, headers and footers, conditional formats and pictures | every sheet, numbers and dates as numbers with their formats, booleans, styles, merges, widths, heights, hidden rows / columns, frozen rows |
| XLS (Excel 97–2003, also Excel 5/95) | ExcelDataReader (MIT): values, number formats, merges; fonts, fills, borders and alignment (BIFF8), widths, heights, hidden rows / columns and the page setup from the BIFF records (`XlsLayout.cs`) | no (LibreOffice) |
| ODS | own reader (`content.xml`, `styles.xml`, `settings.xml`): repeated rows / columns (the empty repeats up to the end of the sheet are skipped), spans, every value type, the text Calc shows (`text:p`), data styles as Excel codes, cell styles, widths, heights, hidden rows / columns / sheets, frozen and header rows | every sheet, typed cells (float, percentage, currency, date, time, boolean) with data styles and the shown text, styles, merges, widths, heights, hidden rows / columns, frozen rows |
| CSV / TSV | delimiter (CSV: comma, semicolon or tab; TSV: tab) and encoding (UTF-8 / UTF-16 with BOM, CP949 when it looks Korean or on Korean Windows, else the PC's code page) | UTF-8 with BOM, CRLF, one file per sheet |

- Number formats: Excel codes and OpenDocument data styles are converted both ways (`OdsNumberStyles.cs`) for
  digits, decimals, grouping, thousands scaling, percent, scientific, currency symbols, literal text, colours,
  positive / negative / zero sections, dates, times and durations. Codes with conditions (`[>100]`), fractions or
  text between digits are written as plain numbers (General).
- Column widths use Excel's unit (a digit of Calibri 11 = 7 px); in ODS a character is 5.25 pt. LibreOffice
  measures with its own fonts, so a width can differ by up to a fifth after LibreOffice opens the file.
- XLS fonts, fills and borders come from the FONT, XF and PALETTE records of BIFF8 files (Excel 97–2003); Excel 5/95
  files keep the default look. XLS output still needs LibreOffice. Password-protected XLS and ODS files stop with a
  clear message. XLSX and ODS packages are read within `DocumentLimits` (sizes, inflate ratio, XML depth and
  elements); ODS repeats expand to at most 20,000,000 cells.
- Not converted to other spreadsheets: formulas (their cached results are kept), charts, pictures, comments,
  conditional formatting, data validation, print areas and page breaks (the print engine reads the page setup,
  print areas, breaks, conditional formats and pictures for PDF).

The XLSX donut also offers **PDF + Edit** (`tool-sheet-studio`). Unlike an ordinary routed XLSX → PDF conversion,
this interactive tool always requires LibreOffice regardless of engine priority: it preserves the original workbook,
makes one disposable copy per selected sheet region, applies that region's print settings, and asks LibreOffice to
render it. PDFsharp combines the resulting PDFs. The live preview follows this exact studio export path and uses
PDFium/PDFtoImage for lazy thumbnails and the selected large page; the ordinary donut's PDF can differ because it
is printed by Filee's own print engine (below).

## Spreadsheet printing

`SheetPrintConverter` (engine id `sheet-print`) prints XLSX (and XLSM / XLTX), XLS (and XLT) and ODS to PDF the way
Excel prints a whole workbook, without Excel or LibreOffice (GitHub Project 4, item *Spreadsheet print layout →
PDF*). The readers above keep each sheet's page setup; `Office/Sheets/Print` lays the pages out with Excel's page logic
and draws them with the Filee renderer's PDF writer (`Rendering/PdfPageWriter`, fonts subset by Filee) and its text
services (FontResolver with font fallback, the HarfBuzz shaper, the UAX #14 line breaker).

- **Route.** Cost 12; the engine is not in the default priority list, which adds 9: 21, below the HWPX writer and
  rhwp (about 31) and LibreOffice's fallback (25 + 9), above LibreOffice when Settings → Engines → *Use LibreOffice
  for spreadsheets → PDF* is on (10 + 9), so that choice still holds. CSV and TSV have no page setup and keep their
  route through the HWPX writer (each sheet fitted to the page width). Its edges aren't `Unshaped`: Arabic, Hebrew,
  Indic and Thai sheets are shaped. Pictures of the pages go on from its PDF through PDFium.
- **Units** (`SheetPagination`, `SheetRowHeights`, `SheetText`). Excel measures at 96 dpi (0.75 pt a pixel) with
  the Normal style's font at a whole pixel size. A column of width *w* (characters, as files store it) is
  Truncate(((256 × *w* + Truncate(128 / mdw)) / 256) × mdw) pixels, mdw the font's widest digit (Calibri 11 → 15 px →
  7 px). Columns without a width use the file's default width, else baseColWidth (8) digits + 5 px rounded up to a
  multiple of 8 px: 64 px (48 pt) for Calibri 11, 72 px for 맑은 고딕 11. ODS widths are points. A row's height is the
  file's (custom, or the autofit Excel and Calc store), else Excel's autofit: one line of a font is its rounded win
  ascent + descent + 2 px (Calibri 11: 14 + 4 + 2 = 20 px = 15 pt; Arial 10: 17 px; 맑은 고딕 11: 22 px), wrapped
  text a line per line in its column's width, rotated text its extent, at least the sheet's default height; merged
  cells don't count. When the Normal font isn't installed, the metrics of Calibri, Arial and Times New Roman are
  known, so the page breaks don't depend on the PC.
- **What prints.** Every visible sheet with something on it (Excel's *Print Entire Workbook*), each with its own page
  setup: its print area (several ranges each start on a new page; whole columns or rows are cut to the used range),
  else its used range — the cells with text, a fill or borders, merged areas and pictures, from the first of them
  (not from A1). Hidden rows and columns take no room. A workbook with nothing to print gives one empty page.
- **Pages.** Scale: *Adjust to* (10–400 %), or *Fit to* N × M pages: the largest whole percent from 10 to 100 at
  which the print area fits (0 = as many as needed); manual breaks don't count then, as in Excel. Each print area is
  cut into columns and rows of pages: before a manual break, and before the column or row that no longer fits inside
  the margins (one larger than a page gets a page of its own). Print titles (rows at the top, columns at the left)
  repeat on the pages after them and take their room; printed headings take theirs. Pages go down, then over (or
  over, then down), centred inside the margins when the sheet asks.
- **Drawing** (`SheetPageRenderer`). Fills (solid; patterns as the mix of their colours; linear gradients), gridlines
  and row / column headings when the sheet prints them, borders (shared with the neighbour, inside merged areas left
  out, hair to thick, dots, dashes, double lines, diagonals), number formats as the readers show them with the colour
  of their section ([Red], [Color10]), numbers too wide for their column as #### (General ones first with fewer
  decimals or in E notation), alignment (General: text left, numbers right, booleans and errors centred; indent;
  fill; justify; distributed; centre across selection; top, centre, bottom, vertical justify), wrap, shrink to fit,
  rotation (−90° to 90°) and stacked text, text running over empty neighbours (to the next cell with text, a merged
  area or the edge of the page; clipped text is left out of the PDF), rich text, right-to-left sheets, pictures
  anchored to cells, and conditional formats that colour cells: comparisons with constants, text contains / begins /
  ends, blanks, errors, top / bottom N or N %, above / below average, duplicate and unique values, two- and
  three-colour scales, with their fills, font colours, bold, italic, underline, strikethrough and borders
  (`ConditionalFormats.cs`).
- **Headers and footers** (`HeaderFooterText`). Excel's codes: &L &C &R sections; &P (with +n / −n), &N (the pages of
  the whole PDF), &D and &T (the PC's short date and time), &F, &Z, &A; &B &I &U &E &S &X &Y; &"font,style", &nn and
  &K; &G pictures (XLSX legacyDrawingHF). Different first page and odd / even pages, scale with document, align
  with margins. The header grows down from its margin, the footer up from its. Pages are numbered on across sheets
  unless a sheet sets its first page number. ODS header and footer regions (with page number, page count, sheet
  name, file name, date and time fields) become the same codes, and their height moves the top and bottom margins
  as LibreOffice does when it saves XLSX.
- **Readers.** XLSX: pageSetup (paper sizes by code and paperWidth / paperHeight, orientation, scale, fit, page
  order, first page number), pageMargins, printOptions, headerFooter and its VML pictures, sheetPr fitToPage,
  _xlnm.Print_Area and _xlnm.Print_Titles (rows and columns), manual rowBreaks / colBreaks, sheetFormatPr,
  stored row heights, rightToLeft, font names, superscript / subscript, double underline, pattern and gradient
  fills, diagonal borders, the whole alignment, rich text, dxf styles with conditionalFormatting, and pictures of
  the sheet's drawing (one-cell, two-cell and absolute anchors). A pageSetup without paperSize means Letter (the
  schema's default, as Excel reads it); a sheet without pageSetup gets the region's paper (A4, or Letter where the
  region measures in inches). XLS (BIFF8): FONT, XF and PALETTE for every cell (blank cells with fills or borders
  too), SETUP, the margin records, HCENTER / VCENTER, PRINTGRIDLINES / PRINTHEADERS, HEADER / FOOTER, page
  breaks, WSBOOL fit to page, the Print_Area and Print_Titles names, DEFCOLWIDTH / STANDARDWIDTH / DEFAULTROWHEIGHT
  and WINDOW2's right-to-left. ODS: the master page of each table's style and its page layout (paper, orientation,
  margins, scale-to percent, pages and X × Y, printed grid and headers, page order, table centring, first page
  number), header and footer regions, table:print-ranges, header rows and columns, page breaks before rows and
  columns, rotation, stacked text, indent, shrink to fit and font names.
- **Hostile input.** Packages are read within `DocumentLimits` (a malformed part is `InvalidDataException`, a bomb
  `error.document_too_complex`); column ranges expand at most 4,000,000 times per workbook, merged areas fill at
  most 10,000,000 index entries, pictures up to 64 MB and `MaxPicturePixels`, header codes up to 4,096 characters.
  Each sheet's page count is checked against `MaxPages` before anything is drawn (a cell in A1 and one in
  XFD1048576 would be about 35 million pages), and the layout runs under `LayoutGuard`. `DocumentFuzzTests` mutates
  an XLSX and an ODS built in code and prints them.
- **Not printed yet.** Charts, shapes, text boxes, comments, sparklines; data bars, icon sets and conditional formats
  with formulas of their own; conditional formats of XLS and ODS files; rich text in XLS cells; text running past the
  edge of a page onto the next one; black-and-white and draft printing; the margins a real printer can't print in
  (a PDF has none). The studio (*PDF + Edit*) still renders through LibreOffice.

`tests/Filee.Engines.Tests/SheetPrintTests.cs` builds workbooks in code and checks the page count and where each page
starts against the numbers Excel's units give (10 default columns and 48 default rows on A4 with the Normal margins,
scale, fit to pages, breaks, print areas and titles, hidden rows and columns, widths and heights, wrapped text,
landscape Letter, XLS and ODS page setups), and the drawing (fills, patterns, gradients, conditional formats and
pictures by their pixels; headers, footers, page numbers, centring, merged cells, overflow, ####, rotation, stacked
and shrunk text, indents, right-to-left sheets by the PDF's text).

## Fonts

Fonts are converted in-process (`Filee.Engines/Fonts`); zlib and Brotli come with .NET. The input format is
recognised by content, so a mislabelled file still converts. FON is the exception: it is chosen by its `.fon`
extension, and the file must then have an NE header with FNT resources.

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
- **FON → TTF, OTF, WOFF, WOFF2, EOT** (input only): Filee reads the monochrome FNT v2/v3 bitmap resources of an NE
  executable and turns each lit pixel into a straight, grid-aligned TrueType outline. The result retains the
  original pixel shape, per-glyph advance, ascent and baseline; there is no smoothing or curve tracing. A FON with a
  single font resource produces `Name.ttf` (or the corresponding OTF/WOFF/WOFF2/EOT extension). Several resources
  produce one file per strike, such as `Name-13px.ttf`, `Name-13px-Bold.ttf`, `Name-13px-2.ttf` and
  `Name-14px.ttf`; the font family names distinguish heights, group different styles at the same height, and number
  only repeated styles at the same height. Existing output files follow the usual Skip, Rename and Overwrite
  policies independently for each strike. Invalid resources fail the whole input before output files are allocated.
  Character mapping uses the Windows single-byte code page indicated by the resource charset, not the computer's
  locale. Undefined bytes (including those that Windows decodes to Private Use code points in the Greek, Hebrew,
  Baltic and Thai code pages), non-round-tripping bytes and Unicode control characters retain their glyphs and
  spacing but receive no text mapping; charset hints cannot recover the meaning of custom glyphs. PE resources, vector or
  externally stored bitmaps, color FNT, standalone `.fnt` files and double-byte or unrecognized charsets are not
  supported. FON cannot be an output format.
- Not supported: CFF2 (variable) outlines to TTF or EOT (to WOFF, WOFF2 and OTF they convert), writing font
  collections, and WOFF/WOFF2 extended metadata (dropped).
- Subsetting (`Fonts/FontSubsetter.cs`) is not a conversion: it serves the Filee renderer's PDF output, see
  [Filee renderer: PDF output](#filee-renderer-pdf-output).

`tests/Filee.Engines.Tests/FontTests.cs` checks every conversion with small OFL fonts in
`tests/Filee.Engines.Tests/Fonts` (see `OFL.txt` there): round trips keep every table byte for byte, a WOFF2 made
by Google's encoder decodes to the source font, and converted CFF glyphs are compared with the originals in Skia.
`tests/Filee.Engines.Tests/FonTests.cs` builds synthetic FON samples in code (`FonBuilder`) and compares every
rendered glyph with its source bitmap.

## Filee renderer: PDF output

Groundwork for Filee's own document renderer (GitHub Project 4, "Filee: built-in engines", item *Layout engine: PDF
backend and font subsetting spike*). The layout engine's page model is drawn through it; the engine itself is a
preview the user turns on (below).

**Recommended decision (for the maintainer to confirm, "Decisions to make" #1): write PDFs with Skia's PDF backend
(`SKDocument.CreatePdf`) and fonts subset by Filee (option A), not with a page writer of Filee's own on PDFsharp's
low-level objects (option B).**

### The engine: "Use the Filee renderer (preview)"

`Typesetting/FileeRendererConverter.cs` (id `layout`, "Filee renderer") reads a document into the document model,
lays it out and writes the PDF in one step, in-process. Settings → Engines → *Use the Filee renderer (preview)* turns
it on (`AppSettings.FileeRenderer`, off by default); off, it has no edges and is shown as off, so every route stays
as it was.

- **What it takes:** HWPX and HWP (read with 한글's stored line layout; HWP through the [HWP reader](#hwp-reader)),
  Word documents (DOCX, DOCM, DOTX, DOTM), ODT, RTF, text,
  Markdown, HTML and MHTML, EPUB, KEPUB, MOBI / AZW3 / AZW / PRC, FB2, HTMLZ, TXTZ, and Pandoc's formats when Pandoc
  is installed, to PDF. Pictures of the pages go on from its PDF through PDFium, which applies the image options.
- **What keeps its route:** spreadsheets ([Spreadsheet printing](#spreadsheet-printing)), presentations (slides), PDF and OFD.
- **Cost 1:** it wins over rhwp and the HWPX writer whatever their place in the priority list.
- **The check before converting** (`ConversionEdge.Checked`, `ICapabilityCheck`): the engine looks through an HWPX, Word
  or ODT package (an HWP as the HWPX package it becomes) for what it doesn't draw yet — OLE objects and charts, vertical
  text, form controls, videos, SmartArt — and `JobQueue` plans again without it, so such a file takes its usual route
  (logged). The choice is made once, before the conversion. A checked edge is only ever a route's first step, where the
  file is the one the engine looked at. Text formats hold nothing it can't draw.
- Its edges aren't `Unshaped`: with it on, Arabic, Hebrew, Indic and Thai documents are shaped by it instead of
  going to LibreOffice.

### How option A writes a PDF

- `Rendering/PageContent.cs` records a page: shaped glyph runs (`GlyphRun`: glyph IDs, positions, the source text
  and HarfBuzz's cluster per glyph), other drawing as `SKCanvas` calls, URL links, links to named destinations and the
  destinations. The same record draws onto any `SKCanvas`, so PNG output comes from the same code as the PDF.
- `Rendering/PdfPageWriter.cs` collects the glyphs every page uses per font, subsets each font once for the
  document, and draws the pages with the subset typefaces into `SKDocument.CreatePdf`. Each run is one text blob with
  its UTF-8 text and clusters (`SKPositionedTextRunBuffer`): Skia writes a Type 0 / CIDFontType2 font with a subset
  tag, a ToUnicode map built from the subset's cmap, and `/ActualText` for clusters that don't map one glyph to one
  character (ligatures, Indic conjuncts and reordered vowel signs, Arabic contextual forms). Right-to-left runs are
  drawn in logical order (glyphs reversed, each at its own position), so text comes out the way it was written in
  readers that take it in content order.
- **Pictures of the pages** (`FileeRenderer.ToImages`, PNG / JPEG / WebP at a given DPI) are drawn from the same page
  record onto a Skia bitmap (`PageContent.Render`), with no PDF → PDFium step. They match PDFium's drawing of the PDF:
  the same glyphs at the same places (SSIM ≥ 0.95 on the matrix's pages; PDFium draws horizontal stems a little darker).
  `ToPdf` and `ToImages` report pages done and stop when cancelled between pages.
- Afterwards PDFsharp adds what Skia can't: the outline (bookmarks, with `/XYZ` destinations) and exact page sizes.
  Skia rounds the media box to whole points (A4 becomes 595 × 842) and draws from its top edge; the media box is set
  to end at that edge (`[0 0.11 595.28 842]`), so nothing moves. Pages of whole-point sizes without an outline skip
  this pass.
- `Fonts/FontSubsetter.cs` keeps the outlines of the used glyphs (and the components of composite glyphs) at their
  original glyph IDs and empties the others; the glyph count is cut to the highest used ID + 1. Kept: glyf / loca,
  head, hhea / hmtx, maxp, OS/2, a cmap of the used glyphs (`Fonts/CmapTable.cs`, formats 4 and 12), post 3.0, name
  records 0–6 and the hinting tables (cvt, fpgm, prep, gasp: some CJK fonts build glyphs with instructions). Layout,
  vertical, bitmap and variation tables are dropped. CFF fonts are converted to TrueType first, only the used glyphs
  (`CffToTrueType`), so Skia always gets a TrueType font. Keeping glyph IDs costs about 1 KB per font after deflate
  (loca and hmtx of the empty glyphs); hinting about 3 KB per font.
- Fonts the subsetter can't take (CFF2, colour fonts) are handed to Skia whole: it draws them as Type 3 fonts (still
  searchable), as it does today.

### What was measured

One A4 page built in code (`PdfPageWriterTests`): Korean (11 pt, 6 lines), English with ligatures and quotes, Arabic
with harakat (right-to-left, right-aligned) and Hindi with conjuncts and reordered vowel signs, four headings with
rules, a link, an outline and a filled box. Latin and Arabic in Segoe UI, Devanagari in Nirmala UI, Korean in the font
of each row. Option B was a prototype on PDFsharp's low-level objects (Type 0 / CIDFontType2, Identity-H,
CIDToGIDMap Identity, its own ToUnicode CMap and per-cluster `/ActualText`, the same subsetter); it drew only text, so
its page had no rules or box. The prototype is not in the repository.

| Korean font | A: PDF | A: fonts embedded | B: PDF | B: fonts embedded |
|---|---|---|---|---|
| Malgun Gothic (TrueType, 13.5 MB) | 72.7 KB | 28.1 + 19.3 + 12.4 KB | 73.4 KB | 27.8 + 19.0 + 12.2 KB |
| Noto Sans KR VF (variable TrueType, 10.4 MB) | 57.5 KB | 12.3 + 19.3 + 12.4 KB | 57.9 KB | 12.1 + 19.0 + 12.2 KB |
| Pretendard (CID-keyed CFF, 1.6 MB) | 57.2 KB | 12.4 + 19.3 + 12.4 KB | 57.7 KB | 12.2 + 19.0 + 12.2 KB |
| S-Core Dream (CFF, 0.7 MB) | 56.0 KB | 11.0 + 19.3 + 12.4 KB | 56.2 KB | 10.9 + 19.0 + 12.2 KB |

- **Without Filee's subsetter**, Skia embeds the whole font: one line of Malgun Gothic makes a 7.45 MB PDF, one line of
  Segoe UI 550 KB, and the variable fonts and CFF fonts above come out as Type 3 fonts.
- **Size**: both options under 200 KB with every font embedded as a subset (`ABCDEF+` tag, FontFile2), under 75 KB
  in fact. The writers' own overhead differs by less than 1 KB.
- **Text**: PdfPig reads back the exact source text from both (content order, `/ActualText` spans replacing their
  glyphs): every cluster whole, Arabic in logical order, with every Korean font.
- **Look in PDFium** (PDFtoImage, rendered at 300 dpi and averaged down to 100 dpi, compared with Skia's own picture
  of the page; SSIM over the windows with ink): A 0.946–0.954 with 0.45–0.5 % of the pixels more than 64 grey levels
  apart, B 0.960–0.971 with 0.07–0.2 %. B also matches PDFium's picture of the same glyphs drawn as paths (0.99),
  A less (0.945). Crops at 300 dpi look the same: the difference is anti-aliasing, most likely because PDFium draws
  Skia's text through a flipped text matrix (`1 0 0 -1`), not glyph shapes or positions (PdfPig finds all 966 glyphs
  of both files within 0.001 pt of each other).
- **Speed**: A 0.17–0.4 s for the page including subsetting (1.1 s for Pretendard, whose 1.6 MB CFF is parsed whole),
  B 0.03–0.17 s.
- **Not checked**: browsers (Chrome and Edge use PDFium, the engine checked; Firefox's pdf.js wasn't run), Adobe
  Acrobat and macOS Preview; the full Noto Sans CJK OTF (not installed here: CID-keyed CFF is covered by
  `FileeTest-CID.otf`, a subset of Noto Sans KR OTF, and by Pretendard locally); Linux and macOS. Set
  `FILEE_RENDER_OUT` to a folder when running `PdfPageWriterTests` to keep the PDFs for a look in those viewers.

### Why A

- Text is a draw, not the hard part: both options write the same small, searchable PDFs, because the subsetter, the
  cluster handling and the logical order do the work in both.
- Everything else a page has, A gets from Skia: paths, strokes and dashes, gradients, clipping, transparency, pictures
  (JPEG passed through, others deflated with soft masks), links and destinations. B would need its own writer for each
  of them, and the PNG route would need a second renderer that has to look the same (the planned check is SSIM ≥ 0.99
  between PNG and PDF). The [EMF/WMF renderer](#emf--wmf-renderer) draws to `SKCanvas` and gets PDF for free with A.
- A's workarounds are small and contained in `PdfPageWriter`: exact page sizes and the outline through PDFsharp, a
  document-wide glyph collection before drawing (the page model is complete by then).

What A can't do, as of SkiaSharp 3.119: tagged PDF (Skia's structure tree API isn't in SkiaSharp), ActualText spans
other than per cluster, and control over the font dictionaries (Skia's subset tags are `AAAAAA+`, `BAAAAA+` … in every
document). If tagged PDF (PDF/UA) becomes a goal, it would need post-processing or option B.

### Still open

- Variable fonts: the subset is the default instance. A non-default instance needs an instancer that applies gvar /
  HVAR deltas before shaping and subsetting. The font resolver doesn't pick one: it uses a variable font at its default
  instance and reports a missing weight as synthetic (see [fonts, shaping and Unicode
  tables](#filee-renderer-fonts-shaping-and-unicode-tables)).
- CFF2 outlines can't be converted or subset yet (Type 3 through Skia). The font resolver uses a CFF2 font only when no
  other font for its script is installed.
- HarfBuzzSharp's `Blob.FromStream` let HarfBuzz read freed memory in the spike's shaper (wrong glyphs, then a
  crash). The layout engine's shaper (`Typesetting/Shaper.cs`) hands HarfBuzz `PageFont.Data` pinned for as long as
  HarfBuzz's face lives.

The spike also fixed CID-keyed CFF fonts whose Top DICT and font dictionary both carry a units-per-em scale (Top DICT
1/2048, font dictionary 1/1000, as in Pretendard): the two FontMatrix scales were multiplied, so OTF → TTF and EOT gave
glyphs a thousand times too small. They are now combined the way rasterizers do (`CffFont.Combine`).

`tests/Filee.Engines.Tests/FontSubsetterTests.cs` checks the subsetter with the small OFL test fonts (same outlines
and advances at the same glyph IDs in Skia, composite components kept, CFF converted, tables dropped, cmap round
trips); `tests/Filee.Engines.Tests/PdfPageWriterTests.cs` builds the page above and checks size, fonts, text and the
PDFium picture, with each installed Korean font (Malgun Gothic, Noto Sans CJK, Noto Sans KR VF; skipped when none is
installed), plus page sizes, links and the outline. The page also passes the render quality harness's read-back checks.

### Render quality harness

How the renderer is measured, against rhwp now and against the PDFs 한글 printed: [RENDER-QUALITY.md](RENDER-QUALITY.md).
A feature matrix of documents written in code, read-back checks of the PDF (text, overlapping glyphs, page and
margins, page count, baselines, embedded fonts), a compare of two renderings (page count, line breaks, per-page SSIM
at 100 dpi) with thresholds kept in `tests/Filee.Engines.Tests/RenderQuality/render-thresholds.json`, and
`build/render-compare.ps1` for a private corpus. The layout engine plugs in at `DocumentRenderers.Candidate`.

## Filee renderer: fonts, shaping and Unicode tables

The text services of Filee's own document renderer (GitHub Project 4, item *Layout engine: font resolver, HarfBuzz
shaper and Unicode tables*), in `src/Filee.Engines/Typesetting`. No converter uses them yet; paragraph layout, CJK
typography and the bidirectional algorithm build on them. They use only what Filee already runs in-process: SkiaSharp's
font manager, HarfBuzzSharp and .NET.

- **Font slots** (`FontSlots.cs`): which of a run's fonts draws a character. 한글's seven language slots (한글, 영문, 한자,
  일어, 외국어, 기호, 사용자) go by script: ASCII, Latin-1 and the spacing modifiers to 영문, quotes, dashes, CJK
  punctuation, fullwidth forms and other symbols to 기호, private use to 사용자, Greek, Cyrillic, Arabic, Thai, ... to
  외국어. Word's four run fonts: ASCII to `ascii`, right-to-left, Brahmic and Southeast Asian scripts to `cs`, CJK
  scripts and blocks to `eastAsia`, the rest to `hAnsi`; with `w:hint="eastAsia"` ambiguous-width characters and
  private use go to `eastAsia`. Word's finer rule for Latin-1 letters in Chinese text (hint plus East Asian language)
  isn't made. A grapheme cluster takes the slot of its first character, so marks stay with their base.
- **Font resolver** (`FontResolver.cs`, `FontNames.cs`, `SystemFonts.cs`): installed fonts through `SKFontManager`
  (DirectWrite, CoreText, fontconfig); nothing is downloaded or shipped.
  - Names: a family is found by its name, by its name without trailing style words ("Pretendard Medium" is weight
    500), and by other names: Korean ↔ English names (함초롬바탕 ↔ HCR Batang, 맑은 고딕 ↔ Malgun Gothic, the HY
    fonts, Nanum fonts; checked against the fonts' name tables), the fonts built into 한글 (한양신명조, 한양중고딕, ...)
    → the HY TrueType fonts of the same design, and metric-compatible replacements (Arial → Liberation Sans, Calibri →
    Carlito, ...). Generic families (serif, sans-serif, monospace) get the language's default fonts.
  - Style: the face nearest the requested weight and slant by the CSS matching rules. A bold or italic the family
    lacks is reported as synthetic (`ResolvedFont.SyntheticBold` / `SyntheticItalic`) for the drawing to fake.
  - Missing fonts and the document-wide rules come from `RhwpFonts.cs`, now shared (`FontSubstitution.cs`): a missing
    font is replaced by an installed one of the script its name points to (else the text's language) and the same
    style (serif or sans); `Plan` swaps Korean default fonts in Chinese or Japanese text (#64) and gives text without
    Hangul, Han or kana one font that has all of it. rhwp keeps using the same rules through `RhwpFonts`.
  - Per-cluster fallback: a run is split into pieces each drawn by one font. A grapheme cluster keeps the run's font
    when it has every character of it, else takes a font already used in the run, a font for its script (a list per
    script: Nirmala UI, Leelawadee UI, Noto, Lohit, ... ; Han by the text's language), then the platform's own
    fallback (`SKFontManager.MatchCharacter`). Punctuation and spaces stay with the font of the text around them.
  - Variable fonts are used at their default instance: HarfBuzzSharp 8.3 has no binding for variation coordinates, the
    PDF subset is the default instance, and Filee has no instancer. DirectWrite's named instances of one variable
    font count as one face, with the weight the font's own OS/2 table gives; a heavier weight is synthetic bold.
  - CFF2 fonts (Source Han VF and the like) are used only when no other font for their script is installed: the
    subsetter can't take them, so Skia would embed them as Type 3 fonts (still searchable, not subset).
- **Shaper** (`Shaper.cs`): HarfBuzz shaping generalised from `Vector/GlyphOutlines.cs`. A run is shaped with up to
  five characters of the paragraph on either side as context (HarfBuzz's own context length), so a word split into two
  runs still joins and reorders as one. Results are in font units, cached by font, features, script, direction,
  language and the run's text with its context, and carry glyph IDs, advances, offsets, HarfBuzz's cluster per glyph
  (monotone graphemes, visual order) and its unsafe-to-break flags for the line breaker. `ShapedRun.ToGlyphRun` makes
  the page model's `GlyphRun` (`PdfPageWriterTests` now builds its page with it). `Shaper.Metrics` gives ascender,
  descender, line gap, Win metrics, x-height, cap height, underline, strikeout and super / subscript metrics.
- **Unicode tables** (`UnicodeData.cs`, `UnicodeData.Generated.cs`): Line_Break, Bidi_Class, Script and
  East_Asian_Width for every code point, as range tables searched by binary search, and the paired brackets of the
  bidirectional algorithm; unassigned code points get the
  UCD's defaults (`@missing` lines: ID for the CJK blocks, R and AL for the right-to-left blocks, ...).
  `build/generate-unicode-tables.ps1` downloads the pinned UCD files from unicode.org into `build/.cache` (SHA-256
  checked, not committed) and writes the C# file, which is committed (Unicode License v3, in THIRD-PARTY-NOTICES.md).
  - Version: **Unicode 16.0.0**, the version .NET 10 uses for `CharUnicodeInfo`, `Rune` and `StringInfo` (checked by
    a test over every code point), so the layout engine's properties and .NET's grapheme clusters and categories agree.
    HarfBuzz 8.3 inside HarfBuzzSharp uses Unicode 15.1 for its own normalization and mark handling; the scripts added
    in 16.0 (Garay, Todhri, Sunuwar, ...) shape with HarfBuzz's default shaper until HarfBuzzSharp moves on. Change the
    version together with the target framework: update `$Version` and the hashes and run the script.

Tests: `FontSlotsTests`, `UnicodeDataTests`, `FontResolverTests` (a fake font list like `RhwpFontsTests`, plus one
check of the installed Malgun Gothic) and `ShaperTests`. The shaper is checked three ways for Arabic, Devanagari,
Gujarati, Thai and Hangul jamo: against HarfBuzz itself (the text shaped the plain way, one buffer with guessed
properties) with every installed font of the script, against recorded HarfBuzz output for the pinned Noto fonts of
`build/fetch-fonts.ps1` (CI fetches them; there is no pinned Gujarati font), and by what each script needs whatever the
font (joining forms and lam-alef, vowel signs drawn before their consonant, conjuncts, the SARA AM split, jamo composed
into precomposed syllables, a dotted circle only for a broken cluster).

## Filee renderer: line breaking and paragraph layout

본 제품은 한글과컴퓨터의 한/글 문서 파일(.hwp) 공개 문서를 참고하여 개발하였습니다. (The layout engine's HWP and HWPX handling was checked against Hancom's published HWP 5.0 file format
document, whose license asks for this notice in the product's interface, manuals, help and source; it is also in
THIRD-PARTY-NOTICES.md, which the About page shows.)

Paragraph layout of Filee's own renderer (GitHub Project 4, item *Layout engine: paragraph layout and line breaking*),
in `src/Filee.Engines/Typesetting`. No converter uses it yet; the render quality harness measures it against rhwp.

- **Line breaking** (`LineBreaker.cs`): UAX #14 at Unicode 16.0, rules LB1–LB31; it passes all 16,672 cases of
  Unicode's `LineBreakTest.txt` (checked when written; the file isn't kept, `LineBreakerTests` covers each rule
  family). Tailorings: 한글 어절 keeps non-Latin words whole (Hangul, and as 한글 does Chinese and Japanese characters,
  East Asian punctuation and quotation marks: a run without spaces is one word, broken between characters only when
  longer than a line), 글자 breaks between syllables as UAX #14 does (한글 writes
  them the other way round from the names: `breakNonLatinWord` BREAK_WORD is 어절, KEEP_WORD 글자; the binary HWP
  of the same document and 한글's own line layout say so); Latin words broken anywhere (`breakLatinWord` BREAK_WORD,
  Word without `w:wordWrap`); a document's own characters forbidden at a line's start or end (Word
  `w:noLineBreaksBefore` / `After`). Hyphenation keeps words whole (no dictionaries), and Thai, Lao, Khmer and Myanmar
  break only at spaces until there is dictionary breaking.
- **Paragraph text** (`ParagraphText.cs`): the inlines as one string (tabs, line breaks as U+2028, links and bookmarks
  kept, a list item's number or bullet in front, followed by a tab when the paragraph hangs) and each character's style:
  the font, 장평, 자간, relative size and 글자 위치 of its 한글 language slot, or Word's run font, scale, absolute
  spacing and raise. 한글's 자간 is a share of each character's own width (장평 applied), not of the font size, so a
  space or a narrowed character closes up less (한글's stored line layout breaks where this puts the line end). 한글's
  "글꼴에 어울리는 빈칸" off (`useFontSpace="0"`, as in Filee's template) makes a space half the size
  wide, and `useKerning="0"` turns the font's kerning off; both are read into `HCharLayout`.
- **Layout** (`ParagraphLayout.cs`, output in `LineBox.cs`): items of one style, font, script and direction are shaped
  (`Shaper`), lines fill greedily to the column width and break at the last opportunity (a word wider than the line
  between clusters), spaces hang at the line end. 최소 공백 (`condense`) keeps a word that fits once the spaces
  shrink; `lineWrap` KEEP and SQUEEZE hold the paragraph on one line. Each line's parts are shaped again with their
  context, so a break inside an Arabic word or after an Indic cluster shapes as it ends.
  - Alignment: left, right, centre; justify widens the spaces (the clusters where a line has none) on all but the last
    line; distribute (배분) widens between all clusters, the last line too. Indents and hanging indents as the model
    has them; the start indent of a right-to-left paragraph is on the right.
  - Tabs: left, right, centre and decimal stops with dot, dash and solid leaders, measured from the paragraph's left
    indent as 한글 does; default stops every 40 pt; a hanging indent is a stop on the first line.
  - Line height by the paragraph's rule: 한글's percent, fixed, at least and between lines measure from the font size
    with the baseline at 85% of the text height (as 한글's stored line segments put it); Word's multiple and 한글's
    "글꼴에 어울리는 줄 높이" from the fonts' OS/2 win ascent and descent.
  - Characters: size, ratio, letter spacing, baseline shift, super- and subscript, colour, shade, underline,
    strike-through, links and bookmarks; synthetic bold and italic (`GlyphRun.Embolden` / `SkewX`) for faces a family
    lacks.
  - Direction: a simplified bidirectional algorithm (strong letters and numbers set the levels, neutrals take the
    direction around them, rule L2 reorders). The runs stay in logical order in the page, so the PDF's text reads as
    it was written. Explicit embeddings, isolates and brackets come with the bidi milestone.
- **한글's stored lines** (GitHub Project 4, item *Layout engine: honor stored HWP/HWPX line layout*): an HWPX that
  한글 saved stores each line's start, height and baseline (`hp:linesegarray`), and 한글 draws from them when it opens
  the file rather than breaking the lines again (checked with 한글: a paragraph whose stored line no longer fits is
  squeezed into it; without the stored lines 한글 breaks it like Filee).
  - The reader converts 한글's text positions, which count every control (tab, field, object, note, equation, section
    and column definition, bookmark) as eight and line breaks and special spaces as one, as Hancom's HWP 5.0 document
    defines them, into positions in the layout's text (`HLineLayout.Starts`). A paragraph a table splits, or a start
    inside an object, keeps no positions.
  - The layout uses them when the lines weren't estimated by Filee and, for text in a font that isn't installed, the
    font standing in for it still fits each stored line (within 8%: Hangul is as wide in nearly every font); otherwise
    it breaks the paragraph itself and says why (`ParagraphBox.StoredLinesIgnored`).
    Each line then takes its stored height and baseline, its place beside an object, and a stored line wider than the
    line is now squeezed between its characters.
  - On the HWPX files attached to Filee's issues, 768 of 769 stored paragraphs convert. The body text of the one
    single-column document breaks exactly where 한글's own PDF of it does; the rest of the pages differ where tables,
    columns and equations are, which later items lay out.
- **Page composer** (`PageComposer.cs`, GitHub Project 4 item *Layout engine: page composer*; `FileeRenderer.cs`
  writes its pages as a PDF):
  - Pages: each section starts a page with its paper, margins and gutter; the body runs from the top margin and header
    area to the footer area; a page break before a paragraph starts a page, a column break the next column (a page in
    a one-column section).
  - Keep rules: keep lines together, keep with next and widow / orphan control (two lines on either page); none moves
    anything off an empty page.
  - 한글's stored layout: lines on it take 한글's page breaks (the stored first-line-of-page flag) and their stored place
    on the page, while the page so far follows it; a paragraph broken again, or a table not as high as 한글 laid it out
    (its line for it, else its stored size), ends that until 한글's next page or column (a column break, or in columns
    a stored place smaller than the last: where 한글 started the next column, Filee does too unless it moved on by
    itself).
  - 한글's numbers: a list number in its own character format (paraHead charPrIDRef), the lines after it hanging under
    the text when the number indents its paragraph (autoIndent); a note's reference in its run's format (an exam's
    endnote numbers are white); a page that hides its header or footer (hp:pageHiding) shows none.
  - Headers and footers: both, odd or even pages by page number, none on a section's first page where it hides them,
    laid out in the header and footer areas. Page numbers count on across sections unless one restarts them; page
    number fields show their page's number, total pages fields the count (a first pass finds it when the body has one).
  - Pictures laid out like characters (no anchor) take their size on the line (their own, else their pixels at 96 dpi)
    and stand on the baseline, 한글's objects set like a character as high as they are with their outer margins (85%
    above the baseline, as 한글's stored lines have them); links, bookmarks and headings (the PDF outline) as before.
  - Models read from other formats (TXT, Markdown, HTML, EPUB, FB2, MOBI, DOCX ...) leave formats to the 한글 style;
    with a `StyleResolver` every paragraph and run is resolved first, as the HWPX writer would write it.
  - Master pages are a later item.
- **Columns, footnotes and endnotes** (`PageComposer.cs`, GitHub Project 4 item *Layout engine: multi-column layout,
  footnotes and endnotes*):
  - Columns: a section's newspaper columns at their gap, with a separator line between them when the section has one;
    text fills a column, then the next, then the next page. A column break starts the next column; 한글's stored lines
    that started a column (the stored first-line-of-column flag) start one.
  - Columns changing on a page (한글 다단 설정 나누기, Word's continuous section break): the columns before are balanced,
    the first ones taking the odd line as 한글 does, and the new ones start under them. At the end of a section they
    are left as they are (neither 한글 nor Word balances there).
  - Note references show their number raised, in the section's note format (한글 각주 모양 / 미주 모양: number type,
    the characters around it); 한글's own numbers, else counted on through the document. The number also starts the
    note.
  - Footnotes go to the foot of the page their reference is on. As 한글 sets them, each is as high as its text from
    its first paragraph's space before, without the spacing below its last line; they stack with the section's space
    between notes, the last ending at the body's bottom, under the separator line (5 cm, 2 cm, a third of the column,
    the column, or a length) with the section's space above and below it. A line whose footnotes don't fit goes on
    with them; footnotes referenced in table cells go to the page the table ends on.
  - Endnotes follow the last section's body on a page of their own, or right under the text after the separator line
    when the section says so (한글 "beneathText").
  - Measured against 한글's own PDFs: hwplib's 각주미주 sample breaks and numbers every line as 한글 (<I>, <II> under a
    full-width line, the endnote on page 2 at 한글's baseline), its separator line at 한글's place and footnotes within
    0.8 pt; hwpxlib's three-column sample has every line as 한글's, on two pages, in the same columns.
  - Not yet: columns of different widths (한글 sameSz="0") share the width equally; footnotes go across the text
    width also in columns (한글 puts them under each column); the separator line is solid; a note's own number is not
    raised where the section asks for it.
- **Tables** (`TableLayout.cs`, GitHub Project 4 item *Layout engine: tables*):
  - Grid: cells take their column (or the next free one past cells spanning down from above) and their row and column
    spans. Columns take the table's absolute widths, else its relative ones (columns without one share the rest), else
    equal shares; tables of other formats (a spreadsheet's columns) are scaled down to the text width when wider, 한글's
    keep the widths 한글 gave them.
  - Cells: padding (the cell's, the table's, else 한글's 1.8 × 0.5 mm), content laid out at the column width less it,
    nested tables included; lines on 한글's stored layout take their stored place in the cell. A row is as high as its
    own height or its tallest single-row cell, whose height is its content without the spacing below its last line (as
    한글 sizes cells); a cell spanning rows stretches its last row. Content sits at the top, middle or bottom.
  - Drawing: fills (a colour, or a picture stretched over the cell: 한글 imgBrush), content, then borders (the cell's,
    the table's, else a thin grid) as solid, dashed, dotted, dash-dot or double lines at their width; captions under
    the table.
  - Pages: rows bound by spanning cells move together; a table that must not split (한글 나누지 않음) moves whole when it
    fits on a page; others split between rows (한글 셀 단위로 나눔) or, where rows may split (한글 나눔 and sources that
    don't say), inside a row's cells unless the row must not split; header rows repeat on every page.
  - 한글's placement: the outer margin (`hp:outMargin`) around the table, the offset of a table placed at its paragraph
    (`vertOffset`), the paragraph a table sits in as a character (the model's empty part before it takes no line; the
    table takes the paragraph's stored place and line spacing), the text after a table that starts its paragraph (its
    stored lines still map, `HTable.OpensParagraph`), and a table that is all its paragraph holds as a character: it
    takes that line's stored place, height and line spacing (`HTable.Line`; 한글 often stacks such tables with a negative
    spacing). A table as high as 한글's line for it keeps the page on 한글's stored places.
  - Measured against 한글's own PDFs (exported by 한글 automation) of the HWPX files attached to Filee's issues: the
    form of #49 (행정정보동의서) breaks every line where 한글 does, on one page as 한글, every line within 0.8 pt of
    한글's; the other form (강사카드) now takes one page like 한글's, 97% of its lines the same.
  - Measured: those formats compose without losing text (read back from the PDF), and some 650 pages of Korean text
    compose in about 6 seconds (the item asks for 640 pages in 30 s; rhwp took some 48 minutes, #127).
- **Floating objects** (`FloatingObjects.cs`, GitHub Project 4 item *Layout engine: floating objects*):
  - Placement as 한글 places objects, measured on 한글's PDFs of written test files: across, against the paper, the text
    area between the margins (PAGE), the column or the paragraph within its indents; LEFT from the left edge, RIGHT the
    offset in from the right edge, CENTER from the centre; INSIDE and OUTSIDE by the page's side. Down, against the
    paper or the body (PAGE) the same way, or from the top of the paragraph's first line (PARA: whatever the alignment,
    never above it).
  - Drawing: pictures with their crop inside their inner margin (`hp:inMargin`) under their outline; rectangles
    (rounded), ellipses, lines, polygons, curves (smooth through their points, as 한글 draws them) and arcs
    (counterclockwise from the first axis point) with a colour or gradient fill and their outline style; rotation and
    mirroring about the centre; text boxes as their shape with their paragraphs and tables inside their padding, at
    the top, middle or bottom. Objects behind the text are drawn under the page's text, the others over it, by z-order.
  - Where: objects of body paragraphs on their page; of table cells, headers, footers and text boxes in their list
    (against the list, or against the paper or page for those that say so), a cell growing and a text box centring to
    hold the objects placed at their paragraph. Pictures, shapes and text boxes laid out like characters take their
    place on the line.
  - Text wrap: lines on 한글's stored layout already leave the room 한글 left. Lines laid out anew go beside objects
    text goes around (on the object's wider side) and below objects text goes above and below; a paragraph's own
    objects are placed before its lines for that.
  - Measured against 한글's PDFs: every anchor of a written test file within 0.7 pt; hwplib's 곡선·호 sample as 한글
    draws it; hwpxlib's rectangle-in-rectangle sample with every line where 한글 has it (SSIM 0.91); a 보도자료 from
    issue files with the ministry emblem in its header cell and every body line within 0.4 pt; a stamped certificate
    with its seals (against the paper, beside the paragraph, in a table cell over "(인)") where 한글 puts them.
  - Sizes: drawings other than pictures take the size 한글 laid them out at (hp:curSz) where it is given, as hp:sz may be
    a share of the column or not grown to the text.
  - Not yet: pictures' effects (shadow, glow, soft edge, reflection), objects' captions, text in a text box turned
    with it, text on both sides of an object, and the distance objects keep from text (outer margins).
- **Equations** (`MathLayout.cs`, `Hwp/Hwpx/EquationParser.cs`, GitHub Project 4 item *Layout engine: equations*):
  - 한글's equation script is read into the equation tree Word math is read into, the way 한글 reads it: command words
    split off the start of a run of letters ("rmA" → rm A, "cupB" → cup B, "itn" → it n), the letters left one
    variable; over and atop between neighbours, sqrt and root … of, LEFT … RIGHT (a missing side empty), matrices,
    cases and piles, sums and integrals with _ ^ or from to, lim, accents, rm / it / bold to the end of their group,
    symbol words and "<=" "!=" "->". Nothing written is lost: what can't be read stays text.
  - Layout with the document's fonts (한글's equation fonts HancomEQN and HYhwpEQ when installed, else Times New Roman
    or another serif, with fallback): italic variables, upright numbers, functions and symbols; relations and binary
    operators spaced unless a sign; scripts at 0.7 raised or lowered; fractions and big operators on the math axis;
    drawn radicals, brackets as tall as their content, accents.
  - On the line, an equation takes the box 한글 laid it out in (hp:sz) with its baseline (baseLine), at its own size
    (baseUnit) and colour, drawn narrowed to the box where Filee's comes out wider (한글 sets equations tighter than the
    fonts' advances); Word's equations take their own box.
  - Measured against 한글's PDFs: two two-column exam papers from Filee's issue files (488 and 249 equations): the
    problems' text and equations on 한글's lines and in 한글's columns, the second paper on 한글's 9 pages, its first
    page as 한글's (the first paper takes 11 pages to 한글's 10); hwplib's 수식 and hwpxlib's SimpleEquation samples
    drawn as 한글 draws them. Filee's equations come out a median 9% wider than 한글's boxes, so they are narrowed.
  - Not yet: 한글's own character widths for equations, equation captions, colour and font changes inside an equation.
- **CJK typography** (GitHub Project 4 item *Layout engine: CJK typography*):
  - No overlapping glyphs in Chinese, Japanese and Korean paragraphs with curly and corner quotes, brackets, book title
    marks and Latin in between: glyphs take the advances HarfBuzz gives with the font that draws them, and with Word's
    `w:hint="eastAsia"` ambiguous characters (curly quotes, dashes, ellipsis) take the East Asian font (`FontSlots`).
    The render matrix's Chinese, Japanese and Korean paragraphs hold such text; rhwp draws ‘ ’ in Chinese over the next
    character there (#237), Filee draws #237's document without overlaps.
  - Line breaking in 한글's 어절 setting keeps Chinese and Japanese runs together like Hangul words, as 한글 does: the
    matrix's Chinese and Korean paragraphs break on 한글's lines and baselines (from 한글's own PDFs; the Japanese one
    as well, its PDF writing Kangxi radicals for some characters).
  - Word's tab stops (measured from the margin) become 한글's (from the paragraph's indent) and back: the indented
    entries of #280's table of contents keep their page numbers on their line at the right margin instead of wrapping
    them onto a line of their own.
  - Not yet: punctuation compression (Word's and 한글's 문장 부호 압축), Word's choice of the East Asian font for
    ambiguous characters by the run's language without a hint, and Word's document grid (lines on #280's grid follow
    the reader's estimate; without Word to check against it is left as it is).
- **Bidirectional text and complex scripts** (GitHub Project 4 item *Layout engine: bidirectional text and
  complex-script line breaking*):
  - The Unicode Bidirectional Algorithm in full (`BidiAlgorithm.cs`, UAX #9 of Unicode 16): the paragraph's direction
    from its first strong character outside isolates, explicit embeddings, overrides and isolates, isolating run
    sequences, weak types (numbers with their separators and terminators, Arabic numbers after Arabic letters), paired
    brackets (`BidiBrackets.txt`, generated with the other tables), neutrals and the reset of tabs and trailing spaces
    to the paragraph's level. It passes every case of Unicode's `BidiTest.txt` (770,241) and `BidiCharacterTest.txt`
    (91,707); the files aren't kept in the repository, `BidiAlgorithmTests` holds the common cases. Lines reorder their
    parts by level; HarfBuzz mirrors brackets in right-to-left runs, and a right-to-left paragraph aligns and indents
    from the right.
  - Thai, Lao, Khmer and Myanmar put no spaces between words, so their lines break between dictionary words
    (`WordDictionary.cs`): ICU's word lists (Unicode License v3), written by `build/generate-word-dictionaries.ps1`
    as sorted, prefix-shared, Brotli-compressed lists (about 500 KB for the four) and loaded the first time a script
    is broken. A run splits into words with as few characters outside a word as possible, then as few words, then as
    few one-character words; characters no word covers stay together. This is how Word breaks them; 한글 has no
    dictionary, and Filee breaks as 한글 does where the document says how (어절: a run without spaces is one word;
    글자: between any two characters), matching 한글's lines for Thai in both.
  - Shaping: Arabic joining, Indic conjuncts and reordered vowel signs, Thai, Lao, Khmer and Myanmar marks come from
    HarfBuzz; the tests check that Arabic, Devanagari and Thai paragraphs are drawn without dotted circles or missing
    glyphs. The render matrix has Lao, Khmer, Myanmar and a right-to-left paragraph with Latin, numbers and brackets:
    text read back in order and no overlapping glyphs. 한글 itself lays such a Hebrew line out against UAX #9 (its words
    come out in reverse order), so Filee follows the standard there.
  - With the renderer preview off, `ComplexScripts` and the `Unshaped` edges still send such documents to
    LibreOffice; on, the new engine draws them. Not yet: vertical text, and Word's and 한글's character spacing for
    Arabic (kashida justification).

The PDF writer's subsets now keep only the characters the text has in their cmap: CJK fonts map 文 and the Kangxi
radical ⽂ to one glyph, and the PDF's ToUnicode took the lower code point, so Japanese and Chinese text copied out as
radicals.

Tests: `LineBreakerTests`, `ParagraphLayoutTests` (with the pinned Noto fonts of `build/fetch-fonts.ps1`), and the
render quality harness, where the renderer is `DocumentRenderers.Candidate`. Against rhwp, Japanese, Chinese, lists
and tabs break into the same lines on the same baselines; rhwp splits some Korean words even in 어절 paragraphs. The
rest waits for the page composer. Against 한글 itself, `HangulLineBreakTests` compares the line starts 한글 stored in
its HWPX files (`hp:linesegarray`) with Filee's, for the plain-text paragraphs of a private corpus
(`FILEE_RENDER_CORPUS`): on the HWPX files attached to Filee's issues, 150 of 152 paragraphs break where 한글 broke
them. Of the other two, one uses a font that isn't installed, and the other is a run of 75 hyphens wider than the
line, which 한글 keeps on one line.

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

## ODT reader and writer

OpenDocument text (LibreOffice Writer's format) is read and written in-process, written by hand from the ODF 1.3
specification, so ODT → DOCX / HWPX / PDF / Markdown / HTML and anything → ODT need neither Pandoc nor LibreOffice.

`Hwp/Hwpx/Odt/OdtReader*.cs` reads `content.xml`, `styles.xml` and `meta.xml` into the document model:

- styles: the default styles, named styles (`office:styles`) and automatic styles, followed through
  `style:parent-style-name` (loops and chains longer than 20 end there). Automatic style names are looked up in the
  part being read: content.xml's for the body, styles.xml's for headers and footers (both may have a "P1");
- pages: each master page becomes a section with its page layout (paper, orientation, margins, columns with their
  separator) and its headers and footers (`style:header` / `style:header-left` as odd / even pages, an empty
  `style:header-first` hides the first page's). ODF's top margin reaches the header and the header's minimum height
  and spacing come before the body, which is the model's Top / Header. A paragraph or table whose style names another
  master page starts a new section, the same one a page break; `style:page-number` restarts the numbering;
- paragraphs: alignment (start / end in right-to-left text), indents, spacing, line height (ODF's proportional line
  height is a share of single lines, about 130 % of the font size as the DOCX reader takes Word's), fixed and
  at-least heights, keep with next / together, widows and orphans, tab stops with leaders, page and column breaks;
- text: fonts (through `style:font-face`), sizes (also in percent of the parent's), bold, italic, underline, strike,
  colour, background, super/subscript, letter spacing; white space as ODF collapses it (`text:s`, `text:tab`,
  `text:line-break`); spans, links (`text:a`, with their character style), bookmarks, page number and page count
  fields; other fields, `text:meta` and ruby keep the text they show. Comments, tracked deletions and hidden text are
  left out;
- headings (`text:h`, outline levels 1–9), lists: number formats (1, a, A, i, I and LibreOffice's 가, ㄱ, ①, 一),
  prefixes, suffixes and the levels a number shows (`text:display-levels`), bullets, start values, nesting, items
  with more than one paragraph and list headers, lists continued after other text (`text:continue-list`,
  `text:continue-numbering`), the list style of a list's paragraphs, the list level's indent (unless the paragraph
  has its own);
- tables: column widths (absolute or relative), spans by covered cells, header rows, row heights and rows kept
  together, cell fills, borders, padding and vertical alignment, tables in cells; text sections with columns
  (`text:section`) change the columns for their content; indexes (tables of contents) keep their text;
- frames: pictures (from `Pictures/` or inline base64, never a file outside the package; SVG / WMF / EMF too) and
  text boxes, as characters or floating with position, relation, wrap, outer margins, border and padding; rectangles,
  rounded rectangles, ellipses (also LibreOffice's custom shapes of those kinds) and lines, with fill and stroke;
  shapes with text become text boxes; formulas (embedded Math objects) become their StarMath text;
- footnotes and endnotes with their numbering (`text:notes-configuration`).

The package is read within DocumentLimits (see **Limits** under *HWPX reader*): `DocumentPackage` counts every part, SafeXml
reads the XML, `DocumentBudget` counts paragraphs, objects and nesting (tables, notes, frames, groups and lists);
repeated columns, rows and cells are cut to the table limits and counted. A password-protected package (one whose
manifest has `manifest:encryption-data`) is refused with a message saying how to remove the password; a package
with another OpenDocument mimetype (a spreadsheet) is no text document.

`Hwp/Hwpx/Odt/OdtWriter*.cs` writes the model back (the package as the ODS writer does: `mimetype` first and stored,
the manifest listing every part): one master page and page layout per section with its headers and footers, named
styles for headings (`Heading 1`..`Heading 9` with outline levels), footnotes, endnotes, headers and footers, one
automatic style per distinct paragraph, text, table, row, cell and frame format, list styles in styles.xml (a list
that goes on after other content continues the first one), tables with covered cells, frames and shapes. Lengths are
written in points with two decimals, exactly one HWPUNIT in 1/100, so an ODT read and written again reads back to
the same model (`tests/Filee.Engines.Tests/OdtTests.cs` builds such an ODT in code and checks ODT → model → ODT →
model, the hostile inputs and, when LibreOffice is installed, LibreOffice reading the written file).

`OdtWriterConverter` (engine `odt-writer`) offers every built-in reader's format → ODT at cost 8, like the DOCX writer,
so it wins over Pandoc and LibreOffice. ODT → PDF goes through the HWPX writer and rhwp until the layout engine ships.

Not yet: numbering restarts in the middle of a list (only an item's start value on the first item counts), outline numbering
of headings (`text:outline-style`), different first-page headers with content, gradients (a gradient fills with its
start colour), rotated or cropped pictures, charts and other OLE objects, drop caps, change tracking, fields other
than page number / count (their text is kept), flat ODT (`.fodt`) and templates (`.ott`, still LibreOffice's).

## PDF reader

`Pdf/PdfDocumentReader*.cs` reads PDFs with PdfPig (Apache-2.0) into the document model, for PDF → DOCX, HWPX and
(with `PdfTextConverter`) TXT and Markdown:

- words with their positions, fonts, sizes and colours → lines → paragraphs: a line continues the paragraph above
  when it follows at about one line pitch, unless it starts with a bullet or number, is indented as a first line,
  or the previous line ended although the next word would have fitted; hyphenated line ends are joined, and no
  space is added between Chinese or Japanese lines;
- tab stops: a line split at wide gaps ("1.  RFP Published   08/14/2026") stays one paragraph with tabs and left tab
  stops where its parts start, unless a column gutter lies between them; a gap counts as a tab after a list number,
  when it is wider than 6 em, or when a row right above or below has a tab at the same place (a form's value column);
- line breaks: in a paragraph narrower than its column (an address block), a line that ended although the next
  line's first word would have fitted keeps its line break;
- reading order: gutters between columns are found where no narrow paragraph crosses; paragraphs that span the
  columns (titles, figures, closing text) split the page into bands, read column by column;
- text that needs shaping (`PdfDocumentReader.ComplexText.cs`): an `/ActualText` of marked content replaces the
  glyphs it covers on every page (LibreOffice writes one for each cluster whose glyphs don't map to its characters
  one to one: conjuncts, vowel signs drawn before their consonant, ligatures). On a page with Arabic, Hebrew, Indic,
  Thai, ... text, words are built from the glyphs in content-stream order instead of by position, because marks
  have no width and overlap their letter: a mark joins the letter before it, a vowel sign drawn before its consonant
  (Word's PDFs keep that visual order) joins the consonant after it, and the dotted circles of PDFs made by rhwp
  (mapped to U+0002 or to a vowel sign, wider than any mark) are left out. Right-to-left words are read from the
  right, with numbers and Latin words in them left to right, a line's words likewise; a paragraph of mostly
  right-to-left lines is marked right to left (`HParaFormat.RightToLeft`), its alignment and indents measured from
  the right, and DOCX gets `w:bidi` on it and `w:rtl` on its Arabic or Hebrew runs (HWPX has no right-to-left
  paragraphs). Fonts keep their family names (`ArialUnicodeMS` → Arial Unicode MS, `NirmalaUI` → Nirmala UI,
  `NotoSansGujarati` → Noto Sans Gujarati). Not recovered: a cluster drawn as one glyph whose ToUnicode entry is
  wrong, in a PDF without ActualText (some Word PDFs), and the word spacing of rhwp's broken layouts;
- headings from font sizes (short paragraphs clearly larger than the body text, the largest size is level 1), bold
  and italic from the fonts, colours, links from link annotations, alignment, indents, spacing and line pitch;
- pictures: JPEG streams are kept as they are, others are decoded to PNG, and what PdfPig cannot decode is cut out
  of the rendered page; a picture covering the page behind text (the scan under an OCR layer) is skipped;
- ruled tables (`PdfDocumentReader.Tables.cs`): tables drawn with lines become real tables in their place in the
  reading order. The rules come from the page's vector paths: stroked straight lines and rectangle edges, and thin
  filled rectangles (up to 4 pt, the way Word and many generators draw borders). Rules on one line (within 1.5 pt)
  that overlap or leave a gap of at most 2.5 pt are joined; horizontal and vertical rules that meet or cross form a
  grid, and grid lines closer than 3 pt are one edge. The edges give the columns and rows (the column widths and
  minimum row heights of the result); where a rule covers less than half of the edge between two grid units, the
  units are one merged cell (column and row spans). A table without side or bottom rules still reaches as far as
  most of its rules do. Words go into the cell under their centre and are read like the rest of the page
  (paragraphs, line breaks, alignment within the cell, fonts, sizes, bold / italic, colours); each cell gets the
  borders that are drawn around it (weight, colour, dashed or solid), shading from a filled rectangle under it, and
  a vertical alignment from where its text sits. Only clear tables are rebuilt: at least 2 rows and 2 columns,
  every merged cell a rectangle, no rule through the middle of a word, and text in at least two cells. A box around
  a note, underlines, separators and empty grids (a chart's frame) stay as they were. Not rebuilt: tables without
  rules (aligned by white space only, or with only horizontal rules), nested tables (their text stays in the outer
  cell), rotated pages; a table running over several pages becomes one table per page. PDF → TXT keeps the cell
  text in reading order;
- page size per page and margins shared by the document; what sits at the top or bottom edge only because it is a
  page is left out: running headers and footers that repeat on most pages (digits ignored, so "Page 3" matches
  "Page 4"), running heads that change with the chapter (the same text at the same height on three pages or more,
  no larger than the body text, so a heading that starts several pages stays), bare page numbers ("12", "- 12 -",
  "Page 3 of 10", "第 3 页", roman "iv") and the same picture (a logo) at the same place on most pages;
- a page without text (a scan, a page of drawings, a turned page) becomes the rendered page (PDFium, 150 dpi JPEG)
  as a picture behind the text of an empty page, so nothing is lost; there is no OCR. PDF → TXT includes the
  invisible OCR text layer of scanned PDFs that have one.

Password-protected PDFs fail with a clear message. Not converted: tables without rules (their cells come out as
paragraphs in reading order), other vector drawings on text pages, form fields and annotations other than links.

**PDF → Markdown** is one step (`PdfTextConverter`, a `DirectOnly` edge, so PDF → HTML, EPUB, ... never start going
through a Markdown file): the document is written by `MarkdownWriter` as for e-books, with headings (`#`, not bold
inside), bold / italic and links. Indents are the PDF's layout, not block quotes, and the PDF's information title
(often a file name) gives no front matter. A Markdown file can't hold pictures, so `Text/MarkdownPictures.cs` writes
them into a folder next to it, reserved through the output allocator like any output (`report.md` →
`report_files/img0001.jpg`, `report_files (2)` when that folder is taken), and links them relatively, so editors
show them and the pair can be moved together; JPEGs are the PDF's own, a scanned page is the picture of the page.
The preset option *Leave out pictures* (`DocumentOptions.LeaveOutPictures`, Documents card, shown for Markdown)
writes the text only, read like PDF → TXT (the invisible OCR layer of scans included) and without a folder. The same
option applies to every Markdown writer: e-books (`EbookConverter`), HWP / HWPX (`UnhwpConverter`) and Pandoc (a
Lua filter drops the pictures, figures keep their captions).

## OFD reader

OFD (GB/T 33190-2016) is China's fixed-layout format: fully digitalized e-invoices (数电票), contracts and official
documents. `Ofd/OfdReader*.cs` reads it into the document model with no external engine, so OFD → DOCX and HWPX are
one step, → PDF two (rhwp), and TXT, Markdown, HTML, EPUB follow from there. OFD is read only (`Writable: false`) and
sits on the office donut (settings schema 27 teaches existing donuts `.ofd`).

- **Package** (`Ofd/OfdPackage.cs`): OFD.xml → the first document (Document.xml) → its pages, template pages and
  resource files (PublicRes, DocumentRes, a page's PageRes, and the unnamed `Res.xml` WPS writes next to them).
  Parts are looked up by name inside the ZIP and never written out under their own names; references are resolved
  against the referring part (or the package root for "/…"), "." and ".." applied, and anything that leaves the
  package, a URL or a drive path finds nothing. The package is read like HWPX and DOCX (`Documents/DocumentPackage.cs`,
  see [HWPX reader](#hwpx-reader)): XML without DTDs; a part may unpack to 64 MB, a picture to
  32 MB (larger ones are left out), the whole package to 1 GB; at most 100,000 parts, 10,000 pages, 2,000,000
  drawing objects, page blocks nested 32 deep. DeltaX / DeltaY "g n d" repeats never yield more values than the
  text has characters.
- **Encryption**: a package with an `Encryptions.xml` (GM/T 0099) or encryption entries in OFD.xml is refused with a
  clear message; nothing is decrypted. Parts that are not XML (encrypted or damaged) fail the same way.
- **Text**: TextObjects with their Boundary, CTM (scale, also of the font size), Size in millimetres, TextCode X / Y
  (missing ones continue where the previous code ended) and DeltaX advances; DeltaY alone moving the characters is
  vertical text. A DeltaX list shorter than the text goes on with its last value while the characters are as wide
  (a title with one advance for all its characters); a step back with DeltaX or down with DeltaY inside one TextCode
  starts a new line, kept with a line break (the four lines of a VAT invoice's 密码区 cipher text are one TextCode).
  Fonts from the resource files, with common Chinese names written in English (宋体 → SimSun, 黑体 → SimHei,
  楷体 → KaiTi, 仿宋 → FangSong, 微软雅黑 → Microsoft YaHei, …) so Word and LibreOffice find them on any system; bold
  from `Weight` ≥ 600 or the font, italic, colour from the object (FillColor element or attribute; RGB, grey, CMYK,
  decimal or #hex channels), its DrawParam or the layer's DrawParam (tax invoices colour the template that way),
  following `Relative` chains. Invisible objects (`Visible="false"`) are left out.
- **Templates**: the template pages a page names are read first, so an invoice's labels (发票号码：, 名称：) and its
  values come out together. In template content, single characters stacked in one column (购买方信息, 备注) or a
  pair spread over a line (合　计) become one word, once the page's own text is there: a pair with a value between
  (共 4 页 of a sales list) stays apart.
- **Annotations** (Annots): the text and pictures of each annotation's appearance, placed from its boundary (a stamp
  drawn as a picture, a note). Hidden annotations and their paths (highlights, underlines) are left out.
- **Seals** (`OfdReader.Seals.cs`): an e-invoice's or a contract's seal is stored in its signature
  (`Signs/Sign_n/SignedValue.dat`, GM/T 0031 SES_Signature). The seal picture in it is shown where the signature's
  StampAnnot puts it, floating in front of the text. PNG, JPEG, GIF and BMP are kept; a seal that is a small OFD
  package of its own (the red oval of 全国统一发票监制章 with its characters set round the edge) is drawn with
  SkiaSharp at 12 pixels per mm on a transparent PNG: text per character with its CTM rotation, stroked and filled
  paths (lines, Béziers, arcs) and pictures. The DER is walked with every length checked against what is left,
  nesting and the element count capped; a SignedValue is read up to 4 MB, a seal package unpacks to 16 MB, at most
  64 signatures with 16 stamps each. The signature is not verified: what shows is the picture the file carries.
- **Layout** (`OfdReader.Layout.cs`): fragments on one baseline form a line, in reading order top to bottom and
  left to right; a line that ran to the right edge from at least half the measure continues in the next one
  (justified, first-line indent kept, no space between Chinese lines). Wide gaps (1.5 em) become tabs with left
  tab stops where each part starts, so an invoice's item rows keep their columns; a line under such a row whose
  parts start in its columns and hold no amounts is the wrapped end of its cells and follows after a line break in
  the same paragraph. Centred and right-aligned lines, left indents and vertical distances (space before) follow
  the page; page size per page (a new section when it changes), margins from what is drawn. Line distances are
  exact where the largest text fits ("at least" grows each line to the font's own height in Word and LibreOffice,
  ~1.4 em for KaiTi, and a full 210 × 140 mm invoice ran onto a second page); a one-line paragraph closer to the one
  above than 1.2 sizes gets the distance between them. A block that starts above the end of the one before (the
  invoice number beside the title) is pushed down, and the next gaps take that back, so the frame stays at its
  place. A table gets its distance from the block above through a 1 pt paragraph, and is `Tight` in HWPX: no 5 mm
  under it and no empty line in the paragraph that holds it. The first block's distance from the top margin is an
  empty line of that height (rhwp draws no space before at the top of a page).
- **Ruled tables** (`OfdReader.Tables.cs`): the approach of the PDF reader in millimetres. PathObjects
  (AbbreviatedData M/S/L/Q/B/A/C, with Boundary, CTM, line width and stroke colour from the object or its
  DrawParams) give stroked straight rules and thin filled rectangles; joined rules that meet form grids, and a grid
  of at least 2 × 2 units with rectangular merged cells and text in two cells becomes a table with the page's
  column widths and row heights, borders in the drawn colour and shading. Text goes into the cell of its first
  character (widths without DeltaX are estimated) and is laid out like the page, with exact line spacing so the
  rows keep their heights. The frame of an e-invoice becomes one table: buyer and seller blocks, the item list
  (one cell with tabbed rows: the invoice draws no rules between item columns), totals and remarks. A cell with one
  paragraph about as far from its top as from its bottom is centred; any other keeps its text's distance from the
  top as the cell's top padding (space before a cell's first paragraph is not drawn by every renderer), and only
  lines at about single spacing (1.6 sizes) are one paragraph. So the columns of a sales list (销货清单: rules only
  between the columns, each column one cell with all its rows) keep their rows level, the 小计 at the bottom of 序号
  beside its amount.
- **Pictures**: ImageObjects (MultiMedia of type Image) with the box from their CTM. PNG, JPEG, GIF and BMP are
  kept as they are (the format comes from the bytes, not the name), TIFF and JPEG 2000 are converted to PNG
  (ImageMagick, at most 100 megapixels), anything else (JBIG2) is left out. A picture alone in its band of the page
  becomes a paragraph; one beside or over text (a QR code next to the title, a seal on a table) floats at its place
  on the paper, in front of the text.

Not converted: signature checks (a seal shows whether or not its signature is valid), seal pictures in SVG,
embedded fonts (the font name is kept), composite objects and other vector drawings (the ⊗ before an invoice's
amount in words), attachments, more than the first document of a package, tables without rules (their text stays
in tabbed rows), clipping. Two text blocks side by side without rules share their lines. Renderers place text
differently inside an exact line (Word and LibreOffice at its foot, 한글 and rhwp at its head), so in the rhwp PDF
text sits up to 0.2 sizes higher than on the page. LibreOffice adds cell borders to row heights: the DOCX of a
full 140 mm invoice opened there puts its last line on a second page (rhwp's PDF keeps it on one).

## PDF → XLSX

`Pdf/PdfTablesConverter.cs` puts the tables of a PDF into a workbook (#267). The tables are the ruled ones PDF → DOCX
finds (`PdfDocumentReader.Tables.cs`, above): drawn with lines, with their merged cells, column widths and row
heights. Text outside the tables is left out, as the XLSX preset's hint says; tables aligned by white space only are
not found.

- **One sheet per table** ("Table 1", "Table 2" …). A table that goes on at the top of the next page with the same
  columns (widths within 5 %), right after one that ended its page, with no text between them, joins it on one
  sheet; a header row repeated on the new page is left out.
- **Numbers and dates** become real values with a number format that shows them as the PDF did: `1,234.50`
  (`#,##0.00`), `(120)` (negative), `$9.99` / `€1,000.00` / `₩` `£` `¥` `₹` (the symbol in the format), `12.5%`
  (`0.0%`), and dates with the year first, `2026-10-23`, `2026/1/5`, `2026. 10. 23.` (Korean style). Everything else
  stays text: a leading zero (codes, phone numbers), more than 15 digits, a decimal comma (`1.234,50`, which can't be
  told from thousands), day- or month-first dates.
- Every cell gets thin borders (the table was drawn with lines), its shading, vertical alignment and bold when all
  its text is bold; text wraps. Merged areas keep their borders on every covered cell.
- A PDF with no such table, or a scan (no text), fails with `error.pdf_no_tables` instead of writing an empty
  workbook. The same tables can be written as ODS, or as CSV (one file per sheet). These edges are direct only: an image
  made into a PDF has no ruled tables, so no picture reaches a spreadsheet through PDF.

## PDF → PPTX

`Pdf/PdfSlidesConverter.cs` turns a PDF into a PowerPoint deck of pictures: every page is rendered (PDFium, as for
PDF → images) and becomes one picture on its own slide. The slides look exactly like the PDF but are **not
editable**: there are no text boxes to change, the PDF's layout is not rebuilt. PDF → DOCX is the editable route.

- **Slide size** is the first page's own size (an A4 portrait PDF gives A4 portrait slides, a PDF exported from a
  16:9 deck gives 16:9 slides), within PowerPoint's limits of 1 to 56 inches a side. Every page, also one of another
  size or orientation (pages turned in the PDF are shown turned), is fitted onto the slide without cropping and
  centred on white.
- **Resolution** is the preset's render DPI, at least 150 and at most 600; a page larger than 25 megapixels at that
  resolution (a poster) is rendered at a lower one. The preset's page range picks the pages.
- **Pictures** are JPEG at the preset's quality (90 by default, as the "PPTX (slides)" preset sets it). Pages are
  rendered on white, as PDF viewers show them, so they have no transparency that would need PNG, and JPEG keeps a
  long deck at a size that is easy to send (an A4 text page at 150 dpi is a few hundred KB).
- **Speaker notes** hold the page's text, read like PDF → TXT (reading order, the invisible OCR layer of scans
  included), so it can still be searched and copied. Pages without text get no notes; when PdfPig cannot read a PDF
  that PDFium renders, the deck is written without notes.
- The package (`Pdf/PictureDeckWriter.cs`) is written by hand: presentation, one slide master with a blank layout,
  a theme, the slides with their pictures, a notes master and notes slides, and the document properties. The tests
  validate it with the Open XML SDK and read it back with Filee's own PPTX reader.
- The edge is `DirectOnly`: the route planner uses it only for a PDF converted to PPTX. A Word document or an image is
  never turned into picture slides through PDF, and a picture deck is never a step towards ODP, PPSX or another
  format (a test checks that every other pair of formats keeps its route).

Password-protected PDFs fail with the same message as PDF → DOCX.

## PDF compression

"Compress PDF" (and any PDF → PDF preset with *Compress* set, also with a page range or one file per page) makes a
PDF smaller while it stays a PDF, for e-mail attachments and upload limits. `Pdf/PdfCompressor.cs` does it with
PDFsharp and Magick.NET; nothing has to be installed.

| Level | Pictures scaled down to | JPEG quality |
|---|---|---|
| Smallest (`Screen`) | 72 dpi | 55 |
| Balanced (`Ebook`, the preset's) | 150 dpi | 65 |
| High quality (`Print`) | 300 dpi | 85 |

- **Pictures**: `Pdf/PdfPictureUses.cs` follows the content streams of the pages, of the forms they draw and of
  annotation appearances (`q`/`Q`/`cm`/`Do`) to find the largest size each picture is drawn at. A picture drawn at more
  than 1.5 × the level's resolution is scaled down to it (as Ghostscript does); a picture whose drawing can't be
  followed is taken as large as the page holds it, so it is never scaled down too far. Photos are saved as JPEG at the
  level's quality, pictures with up to 256 colours (charts, screenshots, logos) losslessly (Flate with PNG
  prediction). A JPEG that keeps its size is encoded again only when that saves at least 10 %. A picture is replaced
  only when the result is smaller.
- **Which pictures**: 8 bits per component, gray, RGB (also ICC-based and calibrated) and CMYK, stored as JPEG, Flate
  (with or without PNG prediction), RunLength or uncompressed. CMYK JPEGs, 1-bit pictures and masks, indexed
  (palette) pictures, spot colours, JPEG 2000, JBIG2, CCITT and pictures with a colour-key mask stay as they are.
- **Transparency**: a soft mask is scaled with its picture and kept lossless, so transparent parts stay transparent.
- **Duplicates**: a picture stored more than once with the same data (a logo embedded again on every page) is kept once.
- **Everything else**: every stream is deflated at the highest level when that's smaller; page thumbnails and other
  applications' private data (`PieceInfo`, such as Illustrator's editing data) are removed, as are unused objects.
  When the preset doesn't keep metadata, the XMP metadata of pages and pictures goes too. The document's own XMP is
  written afresh from its document information by PDFsharp, as for every PDF it saves.
- **Fonts**: `Pdf/PdfFontSubsetter.cs` subsets embedded fonts again to the glyphs the pages draw, so a font embedded
  whole, or the fonts of a page split off a long document, keep only what is left. Glyph IDs stay where they were
  (TrueType through `Fonts/FontSubsetter.cs`, CFF through `Fonts/Cff/CffSubsetter.cs`, which empties unused glyphs and
  subroutines and keeps CFF), so text, widths and copy-paste don't change. Handled: Type 0 fonts with an Identity
  encoding (TrueType, CID-keyed or name-keyed CFF, OpenType) and simple TrueType fonts (their own cmap and glyph names
  stay, and every glyph a code may reach through them is kept). `Pdf/PdfFontUses.cs` reads which codes each font draws
  in the pages, forms, annotation appearances, patterns, soft masks and Type 3 glyphs, with a lexer of its own (PDFsharp's
  turns hex strings that start with FE FF into text). A font stays whole when its use can't be followed everywhere: in
  the interactive form's default resources (viewers type new values with them), set by a graphics state, listed in
  resources Filee didn't read, or in a content stream that didn't parse. Simple Type 1 and CFF fonts, Type 3 fonts and
  fonts with a CMap of their own stay as they are.
- **Object streams**: `Pdf/PdfObjectStreams.cs` packs the saved file's objects that aren't streams (pages, fonts,
  annotations, outlines) into deflated object streams with a cross-reference stream (PDF 1.5), which PDFsharp reads
  but doesn't write. Encrypted files stay as they are.
- **Unchanged**: text, vector drawings, links, bookmarks and form fields. The whole file is compressed as it
  is; a page range or single pages are first copied into new PDFs, as without compression (which leaves bookmarks and
  form fields behind), and so is a PDF locked only against changes.
- **Never bigger**: when nothing gets smaller (a PDF of text only, one compressed before), the PDF is written as it was
  and the toast (and `filee convert`) says that it couldn't be made smaller. Password-protected PDFs fail with the same
  message as the other PDF conversions.
- **Ghostscript** (optional, *Also try Ghostscript* in the preset): when it is installed, the PDF is also run through
  `pdfwrite` with `-dPDFSETTINGS=/screen`, `/ebook` or `/printer` (the same 72, 150 and 300 dpi) and
  `-dCompatibilityLevel=1.6`, and the smallest of the two results is kept; when Ghostscript fails, the built-in result
  stands. It is off by default: Ghostscript rewrites the whole file (fonts are subset again, structure and form fields
  may change), so it is the user's choice. Against Ghostscript's `/ebook` on PDFs generated in the tests, the built-in
  result is far smaller where there is much to gain (a font embedded whole: 0.71 of Ghostscript's size; the photo report
  of the tests: 0.02, where Ghostscript keeps its picture at 300 KB), and 2–12 % larger on PDFs that are already compact
  (Filee's own renderings), where Ghostscript writes the content streams again more tightly. The option stays for those.
- **Not done**: object streams (PDFsharp writes none), font subsetting or removal of unused glyphs, converting
  colour to gray, flattening transparency or form fields, recompressing JBIG2/CCITT scans.

## Media tools

Everyday presets, on the back faces of the images, PDF, video and audio donuts where they fit ("Video from images" is
in the preset library only). Video and audio need FFmpeg.

- **Compress image / video / audio** re-encode smaller (quality 75, never above a JPEG's own and PNG at zlib level 9;
  H.264 at the Small quality, at most 1080p; AAC at the Small quality). A copy in the same format that isn't smaller is
  replaced by the source, with the same notice as Compress PDF (`OutputRule.OnlyIfSmaller`); under "Save as ZIP" the
  source goes into the ZIP instead.
- **Extract audio** copies AAC or ALAC into M4A without an encode (`MediaOptions.CopyAudioWhenPossible`); other sound
  is encoded to AAC.
- **Merge videos** copies the clips with the concat demuxer only when all share the codec setup (ffprobe's extradata
  hash), size, pixel format, frame rate, turn and kind of sound; otherwise every clip is fitted into the first clip's
  frame and encoded, a clip without sound silent for its length. Sound tracks FFmpeg can't decode are skipped: each
  clip gives its first track FFmpeg can decode, and the clips are copied only when that track has the same number in
  every clip. **Merge audio** always decodes and joins the sound
  into one M4A, so no encoder padding or gap is left between files. Both are in `Media/MediaJoining.cs`.
  As a special case, a pair of self-contained `.m4s` files, one with only video and one with only audio, is muxed
  together into one MP4 without re-encoding (in either order). Both codecs and the preset's settings must allow copying.
- **Video from images**: each picture is fitted into 1920 × 1080 as a JPEG, then shown for the preset's seconds with
  crossfades (`xfade`) of 1 s, at most half a picture's time, or cuts. At most `MediaOptions.MaxSlides` (300)
  pictures, not counting downloads that failed; the queue refuses more before any is converted. A filter graph too
  long for a command line goes into a `-/filter_complex` file.
- **Remove metadata** keeps the format. Images lose EXIF, XMP, IPTC, comments and the ICC profile, and a JPEG keeps
  its own quality. Video and audio are copied without global and per-stream tags, chapters and data tracks; an MKV's
  attachments (subtitle fonts) stay, with their file name and type. PDFs lose their document information and every
  XMP packet (`PdfSharpConverter.StripMetadata`), and PDFsharp writes a new one holding only its own name and the save
  date; pages, links, bookmarks and form fields stay.

## PDF watermark, page numbers and password

A preset that writes PDF can finish every PDF it makes (`PdfOptions.Watermark`, `PageNumbers`, `Password`;
`Pdf/PdfFinishing.cs`, PDFsharp only). `JobQueue` runs it (`IPdfFinisher`) on each PDF a file became, whichever engine
wrote it (PDFsharp, rhwp, LibreOffice, Ghostscript …), and once on a merged PDF, so its pages are numbered through.

- **Watermark**: the text in light grey (22 % opacity), bold, along the page's diagonal over the content, at 70 % of
  the diagonal and at most a quarter of the short side high.
- **Page numbers**: "3 / 10", 9 pt dark grey, centred 10 mm above the bottom edge.
- Both go on the page as it is shown: centred in its crop box (the media box when it has none), the right way up
  for its `/Rotate`. The page is drawn on unturned and its `/Rotate` put back.
- Text is drawn as glyph outlines (`Vector/GlyphOutlines.cs`, HarfBuzz shaping, shared with SVG text in PDFs). Any
  script works with a font the PC has for it (Skia's font fallback, else Arial or the default face), and no font is
  embedded, so the PDF grows by a few kilobytes. The text can't be selected or searched.
- **Password**: opens the PDF (user and owner password are the same), AES-256 (`SetEncryptionToV5`). It is saved with
  the preset in the settings file, but left out of a preset export and of the copy history keeps to convert failed
  files again: a retry takes it from the preset again, and fails (`error.pdf_password_gone`) when the preset no
  longer has one. The editor takes at most 127 UTF-8 bytes, all that AES-256 readers use, and turns the box off while
  PDF/A is ticked, since PDF/A forbids encryption.

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

Outlook .msg files are read with MsgReader (MIT, `Email/OutlookMessage.cs`) and rebuilt as a MIME message with
MimeKit: sender, To / Cc / Bcc, subject, sent date, Message-ID, the text and HTML bodies (MsgReader takes the HTML out
of RTF-only messages), attachments, pictures the HTML shows (`cid:`, kept with the body) and attached Outlook items
(as attached .eml messages). That message then goes the EML way below, so MSG → HTML / TXT / ZIP are direct edges
(cost 10; through EML would be 20 and parse twice) and **MSG → EML** writes the rebuilt message. Attachments that only
link to a file elsewhere (`ATTACH_BY_REFERENCE`) are not followed. MsgReader brings OpenMcdf (MPL-2.0, the compound
file reader), RtfPipe (MIT) and UTF.Unknown (MPL-1.1, charset detection for old ANSI messages).

- **EML → HTML**: a standalone page with From / To / Cc / Date / Subject / Attachments and the HTML body (its style
  sheets kept, scripts removed) or the text body; pictures sent inline (`cid:`) become data URIs.
- **EML → TXT**: the same headers and the text body (or the HTML body as text).
- **EML → ZIP**: the attachments (attached messages as .eml) with safe, unique file names.

### MHTML web archives

A web page saved as "Webpage, Single File" (`.mhtml` from Chrome and Edge, `.mht` from Word and Internet Explorer)
is a MIME message like an e-mail: the page and its pictures, style sheets and frames as parts of a
multipart/related body (RFC 2557). `Email/MhtmlArchive.cs` reads it with the same MimeKit parser and writes the page
as **one HTML file** (`MHTML → HTML`, engine `email`); the HWPX and DOCX writers read archives directly through the
same code (`DocumentReaders`), so MHTML → DOCX / HWPX is one step and → PDF two (rhwp), and TXT, Markdown, EPUB and
the other HTML targets go on from the page.

- **The page** is the root of multipart/related (its `start` parameter, else the first part), else the first
  text/html part. Its charset parameter decodes it; without one (Chrome writes none) or with a wrong "us-ascii" over
  8-bit text, the BOM, `<meta charset>` or UTF-8 decide, as for HTML files. Quoted-printable and base64 are undone
  by MimeKit.
- **References** are matched against the parts' Content-Location, as written or resolved against the page's own
  location or its `<base href>` (Chrome: `https://…`; Word: `file:///C:/…/page_files/image001.png` named relatively
  from the page), and `cid:` against Content-ID (Chrome names its style sheets `cid:css-…@mhtml.blink`). These are
  compared as text only: nothing is downloaded, and no Content-Location is ever opened or written as a file.
- **Pictures** of the archive (`img src`, `srcset`, `<picture>` sources, the `background` attribute and CSS `url()`)
  become data URIs; raster only (PNG, JPEG, GIF, WebP, BMP, AVIF, also when saved as application/octet-stream), not
  SVG. Linked style sheets the archive holds are inlined as `<style>` (with their `@import`s, four levels deep);
  `url()`s that point elsewhere (web fonts, remote backgrounds) become `none`, and `<` in CSS is escaped so a style
  sheet cannot end its element.
- **Safety**: the result goes through the e-mail sanitizer (`EmlConverter.SafeHtml`): scripts, frames, forms,
  `<link>`, `<base>`, `<meta>`, inline SVG / MathML, event handlers and `javascript:` links are removed, the
  `Content-Security-Policy` meta allows only inline styles and data: pictures. Links stay when they are http(s) or
  mailto (made absolute against the page), anchors into the page itself become `#id`.
- **Limits**: archives over 256 MB are refused; a picture over 32 MB (decoded) is left out, as is any picture once
  the page's data URIs reach 192 MB (a picture used many times counts each time); 8 MB of style sheet text per page.

Tests (`tests/Filee.Engines.Tests/MhtmlTests.cs`) write archives in code: a Chrome-style one (quoted-printable page
with the charset only in its meta tag, a `cid:` style sheet, pictures by absolute and relative URL and from CSS, one
saved as octet-stream, and scripts, frames, remote pictures, fonts and style sheets that must go), a Word `.mht`
(windows-1252, `file:///C:/` locations, VML fallbacks), a `cid:`-only page in EUC-KR, a picture on disk named by a
`file:` URI that must not be read, and the routes to HTML, TXT, DOCX, HWPX and PDF.

## Data (JSON, YAML, TOML, XML, CSV, vCard, iCalendar)

`Data/DataConverter.cs` (engine id `data`) reads every format into one tree (`Data/DataTree.cs`: objects with their
keys in file order, arrays, text, true / false, integers of any size, floats, TOML dates and null) and writes it
again, so structure and types carry over as far as the target can hold them. Output is UTF-8 without BOM with LF
line ends, indented by two spaces (CSV: like the spreadsheet engine, UTF-8 with BOM and CRLF for Excel). There are no
options: the output is always pretty-printed.

| Format | Library | Read | Written / lost |
|---|---|---|---|
| JSON (`.json`) | System.Text.Json | comments and trailing commas allowed (JSONC) | non-ASCII text as it is; NaN / infinities as text ("NaN") |
| HAR (`.har`, read only, #328) | System.Text.Json (`Data/HarData.cs`) | the HTTP Archive a browser's developer tools save: as JSON for JSON, YAML, TOML, XML and JSON Lines; as a table for CSV (and spreadsheets, Markdown, PDF through it), one row per request of `log.entries`: started, method, URL, status, time (ms), size (bytes), type. Pages, timings and headers stay in the JSON. A file without `log.entries` fails as no HAR | — (not written) |
| JSON Lines (`.jsonl`, `.ndjson`) | System.Text.Json | one value per line, blank lines skipped, read as an array | one compact line per record (the array, or the array inside wrappers, see CSV) |
| YAML (`.yaml`, `.yml`) | YamlDotNet (MIT) | YAML 1.2 core schema: unquoted `true`, `null` / `~`, numbers (`0x`, `0o`, `.inf`, `.nan`) are typed, the rest and anything quoted is text (`yes` / `no` stay text); explicit `!!str` / `!!int` … tags; anchors and aliases resolved (copied), merge keys (`<<`) applied; several documents (`---`) become an array; comments dropped; alias "bombs" (more than 5 million values) and nesting deeper than 256 refused | block style; text that would read back as another type (`"true"`, `"007"`, `""`, YAML 1.1's `yes` / `on`, dates) is quoted; multi-line text as a literal block (`\|`) |
| TOML (`.toml`) | Tomlyn (BSD-2-Clause, TOML 1.1) | dates and times keep their TOML spelling (text in JSON / YAML) | written by Filee: plain keys, then `[tables]` and `[[arrays of tables]]`; **null values are left out** (TOML has none); a root that is not an object goes under `items` (`[[items]]`); integers beyond 64 bits as text |
| XML (`.xml`) | System.Xml | root element = an object with one key; attributes as `@name`, text next to attributes or children as `#text`, repeated elements as an array (**one child is no array**), `<a/>` → null, `<a></a>` → empty text, CDATA as text; all values are text (XML has no types); comments, processing instructions and the DTD dropped, external entities never loaded; the encoding from the declaration (EUC-KR …) | a root that is not one object with one key goes into `<root>`, items of nested arrays into `<item>`; names that are not XML names are escaped (`first name` → `first_x0020_name`, unescaped when read back); `x:name` keeps its namespace when `@xmlns:x` declares it; control characters XML 1.0 can't hold are removed |
| CSV (`.csv`) | Filee (`Office/Sheets/CsvFormat.cs`) | first row = keys, one object per row; plain numbers (not `007`) and `true` / `false` typed, the rest text; delimiter and encoding detected as for spreadsheets | only tables: an array of objects (columns = every key, first seen first; an object inside a row becomes `parent.child` columns, one level only), an object of arrays (columns), an array of arrays (rows) or one flat object (one row); objects that only wrap the array (`{"users": […]}`, `<rows><row/>…</rows>`) are looked through. Anything else stops with a message saying what is not a table |

**`.csv` and `.xml` ownership.** CSV stays a spreadsheet format: the spreadsheets donut, XLSX / ODS routes and
"Merge into PDF" are unchanged. The data engine adds `csv ↔ json / jsonl / yaml / toml / xml` edges, and vCard and
iCalendar each convert to and from CSV. Routes combine: XLSX → CSV → JSON, YAML → CSV → XLSX, vCard → CSV → JSON,
and ICS → CSV → JSON. The spreadsheets donut gets "JSON". `.xml` had no owner before; XML-based formats Filee
converts have their own extensions (`.svg`, `.fb2`, `.dbk`, `.opml` …), so a plain `.xml` file is treated as data
and opens the data donut.

**vCard** (`Data/VCard.cs`, own parser): vCard 2.1, 3.0 and 4.0 → CSV with the columns Full name, Given name, Family
name, Organization, Title, Email, Phone, Address, Birthday, Note (one row per contact; several values joined with
`; `, each with its kind in brackets: `010-1234-5678 (mobile); 02-123-4567 (work)`). Folded lines, quoted-printable
with soft line breaks, `CHARSET` parameters, escapes, groups (`item1.EMAIL`), 2.1's bare types (`TEL;CELL`) and 4.0's
quoted type lists and `tel:` URIs are read; a value without `CHARSET` is UTF-8 when valid, else CP949 (address books
of older Korean phones); UTF-16 files with a BOM are read too. Pictures and nested cards (2.1 `AGENT`) are skipped,
a missing full name is made from the name parts (홍길동, "Ada Lovelace"). CSV → vCard reads those columns (also
`Name`, `Company`, `Mobile`, `E-mail` … and Korean / Chinese headers such as 이름, 전화번호, 이메일, 姓名, 电话) and writes
vCard 3.0 in UTF-8, lines folded at 75 bytes; an address goes into the street field.

**iCalendar** (`Data/ICalendar.cs`, own parser): VEVENTs and VTODOs ↔ CSV; one component per row, with these 14 columns
in order: `Type`, `Title`, `Start`, `End`, `All day`, `Time zone`, `Location`, `Description`, `Organizer`,
`Attendees`, `RRULE`, `EXDATE`, `Recurrence ID`, `UID`. Times use ISO 8601, retaining the original offset;
all-day values are dates only, and a DTSTART TZID is kept in its own `Time zone` column. If a TZID cannot be
resolved, the local time stays floating without an offset. A VTIMEZONE in the same calendar without RRULE takes
precedence; RRULE-based zones are resolved through the system time-zone database instead, so historical years are
not guaranteed to match. CSV → ICS writes the normalized columns as VEVENTs or VTODOs. A numeric offset without a
`Time zone` is written in UTC. Each VTIMEZONE is built from the system rule in force at that zone's earliest timed
Start (or End when no Start); offsets in other years, including past years, may therefore be lost. The original
VTIMEZONE definition is not preserved, and only these columns are supported rather than arbitrary calendar data.
VALARM, X-properties, VJOURNAL, VFREEBUSY, binary attachments and other unsupported properties are lost.

## CAD drawings (DWG, DXF)

Built in, no AutoCAD or ODA software (`Filee.Engines/Cad`). [ACadSharp](https://github.com/DomCR/ACadSharp) reads
and writes the files; Filee's own renderer draws them with SkiaSharp (one drawing routine for PDF, SVG and images).

- **Reading**: DWG from R14 (AC1014) to the AutoCAD 2018 format (AC1032, still current in AutoCAD 2026); DXF R12 to
  2018, ASCII and binary. DWG R13 and older fail with "version not supported". Korean AutoCAD writes pre-2007 DXF with
  the code page name `ANSI_949`, which ACadSharp 3.8 does not map; Filee rewrites that header in memory so the text
  is decoded as CP949 (`CadFile.OpenDxf`, covered by a test that fails once ACadSharp handles it).
- **Writing** (DWG ↔ DXF, and DWG → DWG / DXF → DXF in another version): **AutoCAD 2018 (AC1032)** by default — the
  current DWG format (AutoCAD 2018 and later, DWG TrueView, BricsCAD, ODA tools); 2018 DXF stores text as UTF-8.
  A preset can choose an older version (#489, `CadOptions.Version`): 2013, 2010, 2007, 2004, 2000, or the source's
  own version (older than 2000 becomes 2000). ACadSharp 3.8 can't write a 2007 DWG: chosen, it is written as 2004,
  which AutoCAD 2007 opens; kept from a 2007 source, as 2010. Before 2007, text is stored in the drawing's code page
  (`$DWGCODEPAGE`), so `CadCodePages` picks the one that holds the most of the drawing's text (its own on a tie;
  Korean for Hangul, Japanese for Kana, Big5 before GBK for Chinese, East Asian pages only for East Asian text) and
  writes any character it can't hold as `\U+XXXX` (one `?` for a character outside the BMP). Names count and are
  escaped too: layers, blocks, text and dimension styles, linetypes, layouts and attribute tags, so a Korean layer in a
  drawing with only English text gets `ANSI_949`. Text inside table cells (2004 and later) isn't escaped. In a DWG, the
  header's code page index is ACadSharp's (`KSC5601` / `GB2312`), the DXF says `ANSI_949`; both read back the same. Korean is written as `ANSI_949`, as AutoCAD names it (ACadSharp
  needs `KCS5601` to encode it, so the DXF header is renamed after writing). Objects the chosen version doesn't
  have are left out with a note: tables and arc dimensions before 2004, multileaders and PDF underlays before 2007,
  meshes before 2010. ACadSharp's DWG writer is younger than its reader
  and its DXF writer, so DXF is the safer format to hand on. Objects ACadSharp does not model (proxy and AEC objects,
  some 3D and underlay data) are not written; degenerate dimensions it cannot measure are left out of DWG output
  instead of failing the file.
- **What is drawn**: model space seen from the top (Z is dropped). If model space has nothing visible, the active
  layout (paper space) is drawn instead (for a PDF, every layout, see below). The page is fitted to the extents of
  what is actually drawn — the header's EXTMIN/EXTMAX are ignored, so drawings far from the origin still fill it.
  - PDF: an ISO A3 sheet (A4 or Letter when the preset asks for it), landscape when the drawing is wider than tall,
    10 mm margins (or the preset's margin). Vector paths, text as text with embedded fonts.
  - PDF of a drawing with paper-space layouts (#172): **one page per layout** with something on it, in tab order,
    each fitted like the one-page PDF to the layout's drawing and viewport frames. Inside each viewport model space
    is drawn at the viewport's scale (height ÷ view height) around its view centre, clipped to the frame, under the
    layout's own drawing (title block, notes). Viewports that are off are skipped. When no layout has anything of its
    own besides viewports (a drawing made in model space, whose Layout1 only holds the viewport AutoCAD added), the
    PDF stays one page of model space. Not followed: twisted views (drawn untwisted), views from another direction
    than the top (drawn from the top), layers frozen per viewport, non-rectangular viewports and the layout's plot
    settings (paper size, plot scale). Images and SVG stay one picture of model space.
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
- **Colours of the output** (preset, CAD drawing card, for PDF, SVG and image targets; #411): *As drawn* (above),
  *Black and white* or *Grayscale*. Black and white draws every line, curve, text, dimension, hatch pattern line,
  point, arrowhead, SOLID/TRACE and wide polyline in black, like a plot with monochrome.ctb. Filled hatches (solid,
  gradient and the tint of a too dense pattern) are not filled black, which would hide whatever lies on a light
  background colour: they become the gray of their brightness (Rec. 601 luma), so a light fill stays light and a black
  one black. Grayscale gives every colour the gray of its brightness; lines and text are scaled to 0–160 so the
  lightest (yellow) still prints as a mid gray. The page stays white. Applied while drawing, so PDF and SVG stay vector.
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

## 3D models

`Filee.Engines/Models3D` converts GLB, glTF, STL, OBJ, 3MF and PLY into each other, and FBX into any of them, in
managed code: every reader
fills one in-memory model (`Model3D`: parts with positions, normals, UVs, vertex colours and triangles; materials
with a base colour and a base colour texture) and every writer takes it, so each pair is one step. glTF and GLB are
read and written with [SharpGLTF](https://github.com/vpenades/SharpGLTF) (MIT); STL, OBJ/MTL, PLY, 3MF and the FBX
reader are Filee's own code. No native library (Assimp, the FBX SDK) and no download.

- **Units**: STL, OBJ and PLY have none; they are read and written as **millimetres**, as slicers and CAD programs
  treat them. 3MF has a `unit` attribute (micron to metre) and is written in millimetres. glTF is metres. So a 20 mm
  STL cube is 0.02 m in GLB and 20 mm again in STL. FBX has `UnitScaleFactor` (centimetres per unit, 1 when
  missing). There is no preset option for another unit.
- **Axes** follow Blender's defaults: STL, 3MF and PLY are Z up, OBJ and glTF Y up, so a part standing on a printer's
  bed stands upright in a glTF viewer and comes back on its base. FBX says which axis is up (`UpAxis`, `CoordAxis`
  and their signs in GlobalSettings; the front axis follows, as FBX is right-handed), so 3ds Max's Z-up files stand
  up too.
- **Structure**: node hierarchies, 3MF components and build items are flattened: every mesh instance becomes a part in
  world coordinates (one glTF node / OBJ object / 3MF object per part). Polygons are triangulated (ear clipping, so
  concave faces are right). OBJ negative indices, `\` line continuations and 0–255 vertex colours are understood.
- **Vertices**: binary STL is streamed in blocks and vertices are merged by position (a 1,000,000-triangle STL
  converts to GLB in a few seconds with about 150 MB of memory). STL normals are not kept: GLB written from STL has
  none, which glTF viewers must shade flat; STL output gets facet normals from the winding. 3MF output welds vertices
  by position, so meshes split for normals or UVs stay closed for slicers.
- **Colours**: vertex colours are kept as sRGB and converted to linear for glTF `COLOR_0`; material colours are glTF's
  linear base colour, OBJ's `Kd` as it is and 3MF's sRGB `displaycolor`. glTF materials are written metallic-roughness,
  not metallic, roughness 0.8.

What each format keeps:

| Format | Read | Written |
|---|---|---|
| GLB / glTF | default scene (else all root nodes), triangles, strips and fans, `POSITION`, `NORMAL`, `TEXCOORD_n` of the base colour texture, `COLOR_0`, base colour factor and texture (metallic-roughness or specular-glossiness), double-sided; `.gltf` with embedded (data URI) or separate `.bin` and picture files | one node and mesh per part, 16- or 32-bit indices, PNG/JPEG textures (others are converted to PNG); **`.gltf` is one self-contained file** (its buffer is a base64 data URI, about a third larger than GLB) |
| STL | binary (also with a header starting with "solid") and ASCII (one part per `solid`) | binary, one solid |
| OBJ + MTL | `o` / `g` / `usemtl` as parts, `v` (+ r g b), `vt`, `vn`, faces of any size; `mtllib` with `Kd`, `d` / `Tr`, `map_Kd` (options skipped) next to the file | **the `.mtl` and the textures are written next to the `.obj`** (`name.obj`, `name.mtl`, `name_texture.png`, named like the output); vertex colours as `v x y z r g b` |
| 3MF | objects, components (also in other model parts, as Bambu Studio writes them), build transforms, base materials, colour groups (materials extension); `support` / `other` objects skipped | one object per part, base materials from material colours, a colour group from vertex colours |
| FBX (read only) | binary 6.x–7.x (64-bit records from 7.5, zlib-compressed arrays) and ASCII; mesh geometry with polygons of any size, the first normal, UV, colour and material layer (by polygon vertex, control point or polygon, or all the same; direct or indexed); the model hierarchy from the connections with translation, rotation (every Euler order, pre- / post-rotation, pivots and offsets), scaling and geometric transforms; property templates from Definitions; material `DiffuseColor` and `Opacity`; the diffuse texture embedded (Video `Content`) or named next to the file; animation, skins, blend shapes, NURBS, cameras and lights skipped (the rest pose is read) | — (not written) |
| PLY | ASCII, binary little / big endian; vertex position, normal, UV (`s`/`t`, `u`/`v`, `texture_u`/`texture_v`), colours of any type; face lists with per-corner `texcoord` and face colours; `comment TextureFile`; other elements skipped | binary little endian, one mesh; material colours become vertex colours |

Lost on the way (no place in the target): **STL** keeps only the triangles (no colours, UVs, normals or materials);
**PLY** has no materials (their colours become vertex colours, textures are dropped); **OBJ** has no standard vertex
colours (the common `v x y z r g b` extension is written and read; alpha is lost); **3MF** output has no normals, UVs or
textures; and every format drops what Filee's model has no place for: animations, skins, morph targets, cameras,
lights, metallic / roughness / normal / emissive maps, and lines and points. Draco- and meshopt-compressed glTF fail with
a clear message, as do damaged files ("cut short", an index that does not exist, no triangles).

**Files a model refers to** (OBJ's `.mtl` and textures, glTF's separate buffers and pictures, PLY's `TextureFile`,
FBX's texture names) are read only from the model's folder or a folder below it. A path that climbs out (`../`) or
points anywhere else is looked up by its name in the model's folder instead, so a downloaded model cannot pull a
picture from elsewhere on the disk into its output.

**3D → pictures**: a small software rasteriser (`MeshRenderer`) draws an isometric view from the front right, above,
with one light and flat shading per triangle, vertex colours or the texture interpolated across it, and a z-buffer,
at 2048 px and scaled down to 1024 px for smooth edges, on a transparent background. The preset's image options
(resize, background for JPG, quality) apply as for other pictures.

Tests (`tests/Filee.Engines.Tests/ModelTests.cs`) build a cube, a sphere and a fan of coloured triangles in code and
convert each through every format pair, checking triangle counts, bounds, signed volume (winding), normals, UVs,
colours and materials where both formats keep them; hand-written STL, OBJ, PLY, 3MF and glTF files cover the reading
details, and a generated 1,000,000-triangle STL the speed. `FbxTests.cs` writes FBX files in code (`FbxBuilder`:
binary 7.4 and 7.5, plain and compressed arrays, ASCII): a cube under a moved parent, turned and scaled, with a
templated material and an embedded or neighbouring texture, Z-up files in metres, rotation orders, concave polygons and
materials by polygon, and every cut or flipped byte of a file, which must read or fail with a clear message.

## Geospatial data (GeoJSON, KML / KMZ, GPX, Shapefile, CSV, FIT)

`Filee.Engines/Geo` converts geospatial **vector** data between GeoJSON, KML, KMZ, GPX, Shapefile and CSV in managed
code, with no GDAL and no download. Every reader fills one in-memory model (`GeoModel`: features, each with an optional
geometry and its attributes in file order) and every writer takes it, so each pair is one step; through CSV the data
also reaches the spreadsheet and data engines (Shapefile → XLSX, GeoJSON → JSON, KML → HWPX, …).

- **Always WGS 84.** Whatever the source's coordinate system, results are longitude / latitude in degrees (and a height
  where there is one). A Shapefile in UTM, Web Mercator or Lambert becomes a GeoJSON that opens in the right place.
  Keeping the original system is not an option yet.
- **One file in, one set out.** A Shapefile is read from its `.shp`; the `.dbf` (attributes), `.prj` (coordinate system)
  and `.cpg` (text encoding) are found next to it in any letter case, the `.shx` is not needed. A Shapefile is written
  as `name.shp`, `.shx`, `.dbf`, `.prj` and `.cpg`; the engine returns all of them. The file names always match: when
  the `.shp` name is taken, the next free name is used for the whole set, and a leftover file of the same name (a lone
  `.dbf`) is never written over.
- **Save as ZIP.** A geospatial preset can pack its result into one ZIP (Settings → Presets → *Save the result as a ZIP*,
  or Settings → General to do it for every geospatial result, or `--zip` on the command line): a Shapefile's five files
  travel together, and the ZIP, named by the preset's file name pattern and conflict policy, is the only output. The
  step that writes the files runs in the job's work folder and `JobQueue` packs it afterwards, so the option works for
  any engine and any number of steps.
- **Same format again** (`shp → shp`, `geojson → geojson`) rewrites the file in WGS 84, which is how a projected
  Shapefile is reprojected, and splits mixed geometries (below).

What each format keeps:

| Format | Read | Written |
|---|---|---|
| GeoJSON | FeatureCollection, Feature or a bare geometry; UTF-8, UTF-16 or an old code page; comments and trailing commas; `id` (as the attribute `id`); the 2008 `crs` member for the systems below; attributes as text, numbers, true / false, null (lists and objects become their JSON text); Z | one feature per line, polygons in RFC 7946 winding (outer counter-clockwise), at most ten decimals |
| KML / KMZ | every Placemark wherever it sits (Document, Folders), `name`, `description`, `ExtendedData` (`Data`, `SimpleData`); Point, LineString, LinearRing, Polygon with holes, MultiGeometry (as Multi* when the parts are alike, else a collection), `gx:Track`, `gx:MultiTrack`; altitude; KMZ: `doc.kml`, else the first `.kml` inside | one Document with a Placemark per feature, `name` / `description` from the attributes of that name, all other attributes as `ExtendedData`, `MultiGeometry` for several parts; KMZ is a ZIP with `doc.kml` |
| GPX | 1.0 and 1.1; waypoints (Point), routes (LineString), tracks (LineString, or MultiLineString for several segments); `name`, `cmt`, `desc`, `src`, `sym`, `type`, `number`, `time`; `ele` as the height; `gpx_type` says which of the three a feature was | points as waypoints, lines as one track (several lines are its segments) or, with `gpx_type` = route, as routes; polygon outlines as tracks; only the fields on the left survive |
| Shapefile | Point, MultiPoint, PolyLine, Polygon and their Z types; rings sorted into polygons and holes by direction and containment; `.dbf` text, numbers, true / false, dates; deleted rows skipped; MultiPatch is refused | one Shapefile per kind of geometry present; Z types when any height is present; WGS 84 `.prj`; `.dbf` in UTF-8 with a `.cpg` |
| CSV | a `wkt` / `geom` / `geometry` / `the_geom` column (WKT or EWKT), or longitude and latitude columns (`lon`, `lng`, `long`, `longitude`, `x`; `lat`, `latitude`, `y`; a decimal comma is fine), and `elevation` / `alt` / `z`; other columns become attributes, typed; rows without a usable location are kept without a geometry | `longitude`, `latitude` (+ `elevation`) when every geometry is a point, else a `wkt` column; then the attributes. A dataset that already has a longitude or latitude attribute gets `wkt` so the file reads back unchanged |
| FIT | Garmin activity files: the positions of the `record` messages (with their time and height) as one LineString, or a Point when there is only one, with the sport as the feature's type | not written |

**FIT activities.** A Garmin FIT activity (from a watch or bike computer) is read only. Its recorded positions become
one track (a single point when only one position was recorded); a time or a height is kept on the points that have one
and never made up for the others. The feature gets the sport as its `type` attribute and the start as its `time`
attribute: the earliest session start, else the first record time (for a single point, that point's time). Each target
keeps what it can:

- **GPX**: a track (a waypoint for a single position) with `<time>` and `<ele>` on the points that have them, and the
  sport as `<type>`. A track has no start time of its own, but its points keep their times: only when no point has a
  time and the activity has a start does a notice say the start was left out.
- **KML / KMZ**: a Point for a single position. A track is a `gx:Track` when every point has a time and a height;
  otherwise a LineString, with the point times in a `coordTimes` `Data` entry when any point has one. `type` and `time`
  are `ExtendedData`.
- **GeoJSON**: a Point or a LineString, with the point times in a `coordTimes` property when any point has one, beside
  `type` and `time`.
- **Shapefile**: the point times are left out; `type` and `time` are `.dbf` columns. When only some points have a
  height, the heights are dropped with a notice.
- **CSV**: one row per record that has a time, a position or a sensor value, nine columns: `time` (UTC, ISO 8601),
  `longitude` and `latitude` (degrees), `elevation` (m), `heart_rate` (bpm), `cadence` (rpm), `power` (W), `speed`
  (m/s), `distance` (m). An empty cell means the value wasn't recorded or was marked invalid, or the position was out of
  range.

Left out: heart rate sent in its own messages outside the records, developer fields, laps and pauses (the track isn't
split at them), pace, FIT files that aren't activities (courses, settings, monitoring), chained FIT files (several FIT
files joined into one), and files with a bad CRC, which are refused. A file may be up to 64 MiB and 2,000,000
records.

**Privacy.** The outputs carry positions, times and health values such as heart rate exactly as the watch recorded them.
Check what you share.

**Coordinate systems.** Geographic systems (degrees) and the projections most projected data uses, read from a `.prj`
(ESRI or OGC WKT 1), a GeoJSON `crs` name or an EPSG code: Transverse Mercator (UTM, Gauss-Krüger, state plane, via
Krüger's series), Mercator and Web Mercator, Lambert Conformal Conic (1 and 2 parallels), Albers;
metres, feet and US survey feet. They agree with PROJ to well under a millimetre: `GeoCrsTests` checks fifteen systems against reference
positions computed with pyproj. Anything else (Lambert azimuthal, stereographic, oblique Mercator, WKT 2, geocentric,
local) is **refused with its name** (`error.geo_unsupported_crs`) rather than read as degrees. So is a projected system
whose geographic part has a prime meridian other than Greenwich or an angular unit other than degrees (ESRI's NTF Paris
Lambert zones count their longitudes from Paris in grads): the projection's formulas assume neither, and the data would
land about 170 km from where it is. Coordinates outside the Earth with no usable system (`error.geo_unknown_crs`, a CSV or
GeoJSON in metres, a Shapefile without `.prj`) are refused too.

**Datums are not shifted.** WGS 84, NAD83, ETRS89, GDA, JGD, Korea 2000, CGCS2000, RGF93, SIRGAS and every other datum on
the GRS 80 / WGS 84 ellipsoid agree within a metre or two. A datum on another ellipsoid (Bessel, Airy, Clarke,
International: Tokyo / Korean 1985, DHDN, OSGB36, ED50, NAD27, …) is converted as if it were WGS 84, which can be
tens to hundreds of metres off, and the file carries the note `notice.geo_datum_assumed`.

**Shapefile writing.**

- A Shapefile holds one kind of geometry. Mixed data becomes `name_points`, `name_lines` and `name_polygons` (each a
  full set) with the note `notice.geo_shapefile_split`. A collection is split into its members, each a record with the
  attributes repeated; features without geometry go into the first file as null shapes.
- Polygons are written clockwise, holes counter-clockwise, rings closed; Multi* geometries are one record.
- `.dbf` columns: whole numbers as `N`, other numbers as `N` with the decimals they need (15 at most, 24 wide), true /
  false as `L`, everything else as text (`C`, up to 254 bytes; longer text is cut between characters,
  `notice.geo_text_cut`). A column that mixes numbers and text is text. Field names are cut to 10 bytes, made of letters,
  digits and underscores, and made unique (`population_2020`, `population_2021` → `population`, `populati_1`,
  `notice.geo_field_names`). More than 255 columns keep the first 255 (`notice.geo_columns_dropped`). Features without
  attributes get a record-number field `FID`, since a `.dbf` needs one.
- Reading takes the text encoding from the `.cpg`, else the language driver byte of the `.dbf`, else UTF-8 if the text is
  valid UTF-8, else Korean CP949, else Windows-1252. (Chinese GBK and Korean CP949 can't be told apart without a `.cpg`.)
- The files are limited to 2 GB (`error.geo_too_large`); whole files are read into memory, a few at a time.

**Safety.** XML (KML, GPX) is read without DTDs or external entities, so a file can't read other files or expand into
gigabytes. The tree is built from `XmlReader` in `GeoXml.Load` rather than by `XDocument.Load` (whose time is quadratic
in the depth: 48 seconds for 100,000 nested levels), and elements nested more than 256 levels are refused while reading;
MultiGeometry may nest 32 levels. A document is limited to about 67 million characters and 3 million elements (a file
of tiny elements costs about 25 bytes of memory per character: 40 million characters in a 150 KB KMZ took 1.1 GB before
the element limit, and now stops at about 260 MB). A KMZ whose `.kml` is declared larger than 256 MB is refused before it is unpacked, and the unpacked bytes are
counted as they are read (`LimitedStream`), since a ZIP header can understate its size. Counts and indexes in binary
Shapefiles are checked against the record before anything is allocated, the attribute table is read only as far as there
are shapes, rings are sorted into polygons smallest outer ring first with the cancellation token honoured, nesting of
JSON, WKT and WKT definitions is limited, and every failure is a reason the user can act on: a damaged file, a system
Filee can't convert, a CSV with no coordinates (`error.geo_*` in the language files). Nothing is written when a
conversion fails.

Tests: `GeoFormatTests`, `GeoShapefileTests`, `GeoCrsTests`, `GeoHostileInputTests`, `GeoZipTests` and
`GeoRobustnessTests` in `tests/Filee.Engines.Tests`. The Shapefiles in `tests/Filee.Engines.Tests/Geo` were written by
GDAL (synthetic data; `Geo/make-test-geo.py` recreates them), so Filee's reader and writer are not only checked against
each other. Everything else is written in the tests: `GeoBuilders.cs` has GeoJSON, KML and GPX text and a Shapefile and
dBASE builder that follows the ESRI specification for the cases GDAL won't write, and the Shapefiles Filee writes are
parsed in the tests from the specification, not read back with the engine. `GeoRobustnessTests` reads every file
truncated at several lengths and with random bytes changed; `GeoHostileInputTests` keeps each input that once crashed the
process, took minutes or used gigabytes.

Not here yet: raster data (GeoTIFF, bands), GeoPackage, zipped Shapefiles as input, datum shifts, more projections.

## HWPX reader

`Hwp/Hwpx/HwpxReader*.cs` is the inverse of the writer: it reads an HWPX package into the same document model, so
HWPX (and HWP, whose records the [HWP reader](#hwp-reader) writes out as HWPX first) can be written as DOCX, HTML or
EPUB without Hancom Office or LibreOffice. It reads files saved by 한글, by rhwp's `export-hwpx` and by Filee itself:

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

**Limits.** Untrusted documents must not hang, exhaust the memory or crash Filee. The HWPX, DOCX, ODT, OFD and
HWP 5.0 readers and the layout engine share `Documents/DocumentLimits.cs`; a document that passes one stops with
`error.document_too_complex` and says which:

- the package (`Documents/DocumentPackage.cs`): at most 100,000 parts; a part may unpack to 64 MB, all parts together
  to 1 GB, counted while they unpack (a ZIP header can understate a size), and a part over 4 MB to no more than 200
  times its packed size; parts are found by name inside the package only, and extracted pictures get names Filee
  makes (`hwpx-media-1.png`), never the part's;
- XML (`Infrastructure/SafeXml.cs`, also used for KML and GPX): no DTD and no resolver, so no entity expands and
  nothing outside the package is read; at most 512 levels deep and 5,000,000 elements in a package;
- the document (`Documents/DocumentBudget.cs`): tables in tables, text boxes, notes, groups and fields nest at most 32
  levels; at most 1,000,000 paragraphs and 200,000 tables, pictures and drawing objects; table columns, spans and
  row spans are cut to the table (at most 16,384 columns); Word style chains are followed 20 styles deep;
- pictures (`Documents/PictureSize.cs`): one whose PNG, JPEG, GIF, BMP or WebP header claims more than
  16,384 × 16,384 pixels is left out before anything decodes it;
- layout (`Documents/LayoutGuard.cs`, for the layout engine): at most 20,000 pages, 64 empty pages in a row, ten minutes;
  cancellation and the time are checked per paragraph, and an object larger than the page is clipped and placed.

Embedded fonts (DOCX `word/fonts`, HWPX and OFD font files) are not read; a reader that uses them must pass them
through Filee's SFNT parser (`Fonts/SfntFont.cs`) first. `DocumentHostileInputTests` holds ZIP bombs, deep nesting,
DTDs and huge pictures; `DocumentFuzzTests` changes packages written in code at random (attributes set to extreme
values, elements removed, repeated, renamed or nested, text and bytes damaged) and allows a reader only to read a
file or refuse it with `InvalidDataException` / `ConversionRefusedException`. It runs briefly with every test run and
for a few minutes per reader every night (`.github/workflows/fuzz.yml`); `FILEE_FUZZ_SEED` repeats a run.

## HWP reader

`Hwp/Binary` reads HWP 5.0 (binary 한글) documents without rhwp or Hancom Office, written from Hancom's published
"한글 문서 파일 형식 5.0" document and checked against the HWPX 한글 itself saves for the same file. It does not map
HWP onto the document model itself: the records are written out as the OWPML package 한글 saves (`HwpToHwpx`), and
`HwpxReader` reads that, so both 한글 formats share every mapping, the stored line layout included. The same package
is Filee's HWP → HWPX conversion: the HWPX writer's `hwp → hwpx` edge costs 4, so with the engines' places in the
default order it wins over rhwp's `export-hwpx` (4 + 8 against 10 + 3). HWP → DOCX, HTML, EPUB and Markdown go
through the document model like any other format; HWP → PDF takes the Filee renderer when it is on, rhwp otherwise.

- `CompoundFile.cs`: a read-only compound file (CFB) reader of Filee's own. Every sector number is checked against
  the file, every chain is walked at most once per sector and must be as long as its stream says, the directory tree is
  walked with a stack and visits each entry once, and stream sizes count against the package limits (a stream up to
  64 MB, all of them 1 GB).
- `HwpFile.cs`: the FileHeader (signature, version, flags) and the streams, unpacked with raw deflate when the document
  is compressed (counted while they inflate, with the ZIP packages' ratio limit), split into records (tag, level,
  size; a record longer than its stream is damage). Password, DRM and certificate-encrypted documents, distribution
  documents (배포용, text in `ViewText`) and HWP 3.x files (not compound files) stop with a message; nothing is ever
  decrypted. An `.hwp` that is an HWPX package is read as HWPX. The title comes from `\005HwpSummaryInformation`.
- `HwpToHwpx.Header.cs`: DocInfo → `header.xml`: fonts per language (FACE_NAME counted by ID_MAPPINGS), border
  fills with their solid, gradient and picture fills, character shapes, tab definitions, numberings (7 levels, 10 in
  5.1 files; older files get 한글's three empty extra levels), bullets, paragraph shapes and styles. Paragraph margins,
  spacing and tab positions are stored doubled: `hp:case` gets the true value, `hp:default` the stored one, as 한글
  writes them.
- `HwpToHwpx.Body.cs`: paragraphs (text with its character, inline and extended controls, character shape runs,
  highlighter ranges, line segments copied field by field), section definitions with page, note shapes, page borders
  and master pages (separate parts like 한글's), columns, headers and footers, footnotes and endnotes, auto numbers,
  new numbers, page hiding and page number position, bookmarks, index marks, hidden comments, fields (`%hlk` and the
  others, with their command and name), 덧말 and 글자 겹침.
- `HwpToHwpx.Objects.cs`: tables (cells with spans, sizes, margins and borders, captions), drawing objects (lines,
  rectangles, ellipses, arcs, polygons, curves, text boxes, groups, 글맵시) with outline, fill and transformation
  matrices, pictures from `BinData` (crop against their original size), equations (script, size, colour, baseline)
  and OLE objects (kept in the package; the model leaves them out).

Where 한글's files differ from Hancom's document, what 한글 writes wins: border, outline and tab leader line types
count "none" first and dotted (2) before dashed (3); polygon, curve and rectangle points are interleaved (x, y)
pairs; a list header holds a 4-byte paragraph count; the note separator's length takes 4 bytes; a picture's original
size follows its effects; the newest paragraph shapes add the outline level (for ten levels) at the end. Files written
by rhwp are read too: its note shape layout, line spacing kinds kept only in the older field, and pictures stored
undeflated in a compressed file; its stored line layout is rhwp's estimate, so the package marks it estimated
(`FileeLineSegments`) and the layout engine lays such paragraphs out itself.

Measured against the HWPX 한글 saves for the same document (the document model of both, read for layout): the 41
HWP files of the hwplib samples read the same apart from titles 한글 makes up for files without summary information
(it takes the first text) and a summary field whose stored result 한글 drops; 45 of 46 HWP files 한글 saved from HWPX
documents (the other is a distribution document, refused) and the 24 documents of the render feature matrix read the
same. The stored line layout differs only where 한글's own files leave it out: HWP files saved before 한글 laid the
document out (no PARA_LINE_SEG), and HWPX exports of files other programs edited, where 한글 drops what it considers
stale. `HwpReaderTests` reads HWP files rhwp makes from HWPX written in code into the model of their source;
`DocumentHostileInputTests` and `DocumentFuzzTests` hold the hostile compound files and records.

Not read yet: charts, video and form objects (as in HWPX), memos, change tracking and the extended master pages
(last page, any page) at a section's end. With the Filee renderer on, HWP → PDF takes it (`HwpReader.ReadForLayout`);
otherwise rhwp stays the HWP → PDF engine. Video and form objects are not written into the package, so the renderer's
check doesn't see them in an HWP.

The format document asks that products made with it say so: "본 제품은 한글과컴퓨터의 한/글 문서 파일(.hwp) 공개 문서를
참고하여 개발하였습니다." (already in `HwpxReader.Header.cs`).

## E-books

The built-in e-book engine (`src/Filee.Engines/Ebooks`) reads books into the same document model as the HWPX
writer, so every e-book also reaches HWPX, PDF and images. The document model is written back out by
`XhtmlWriter` (EPUB chapters, HTMLZ, single-page HTML), `PlainTextWriter`, `MarkdownWriter` and `Fb2Writer`.

- **EPUB 2 / 3**: container.xml → OPF → spine; each chapter is read by the HTML reader and starts a new page, links
  between chapters become links to bookmarks, pictures come from the package, title / authors / language / cover
  from the metadata.
- **EPUB output** is EPUB 3 with an NCX for older readers: `mimetype` first and stored, a navigation document from
  the headings, chapters split at level-1 headings and page breaks, one style sheet, pictures in formats every
  reader shows (others become JPEG / PNG), a cover page for covers the content does not show.
- **MOBI / AZW / AZW3 / PRC** (`Ebooks/Mobi`, written from the MobileRead wiki's format description): PalmDB
  records, MOBI header and EXTH metadata, PalmDOC (LZ77) and HUFF/CDIC compression, MOBI 6 `filepos` links and
  `recindex` pictures, KF8 text rebuilt from the skeleton and fragment indexes with `kindle:pos` / `kindle:embed` /
  `kindle:flow` references resolved, and plain PalmDOC (TEXtREAd) books. **AZW4** (Print Replica) gives back its PDF.
- **FB2**: nested sections become headings, poems / epigraphs / citations / tables are kept, note links become
  footnotes, pictures come from the base64 binaries; the XML declaration's encoding (often windows-1251) is used.
  FB2 output nests sections by heading level.
- **HTMLZ / TXTZ** (calibre's zipped formats) are read and written, with their `metadata.opf`.
- **KEPUB** (Kobo, `Ebooks/Kepub.cs`, written from the format's public descriptions): EPUB → KEPUB copies the book
  and changes only the XHTML documents of the spine. Every sentence becomes `<span class="koboSpan"
  id="kobo.P.S">` (P counts the block elements of the chapter, S the sentences in it; a sentence ends at . ! ? …
  followed by a space, or at the full-width 。！？), every picture gets a span of its own, and the body's content
  is wrapped in `<div id="book-columns"><div id="book-inner">`. Scripts, styles, SVG and MathML are left alone; a
  chapter that isn't well-formed XML (after HTML entities such as `&nbsp;` became character references) is copied
  unchanged. Files are named `name.kepub.epub`, which tells Kobo devices to use their KEPUB reader. Other formats
  reach KEPUB through EPUB (Markdown → EPUB → KEPUB); KEPUB → EPUB removes the markup again, and a KEPUB is read
  like any EPUB.
- **Comics**: CBZ, CBR, CB7, CBT and CBC (a ZIP of CBZ files) are opened with SharpCompress; pages are sorted
  naturally ("page2" before "page10"). → PDF has one page per picture sized like the picture (JPEGs are embedded
  as they are), → EPUB is fixed-layout, → CBZ repacks. PDF → CBZ renders the pages with PDFium at the preset DPI.
- Books with DRM (Adobe, Apple, Kindle) are refused with a clear message; Filee does not remove DRM. KFX and
  Topaz books are recognised and refused too.

## Calibre

calibre's `ebook-convert` (GPL-3.0) is an optional download for the formats Filee does not read or write itself:
LIT, LRF, CHM, PDB, PML, RB, SNB, TCR and OEB → EPUB, and EPUB → MOBI, AZW3, LIT, LRF, PDB, PML, RB, SNB and TCR.
EPUB is the hub, so for example FB2 → AZW3 is FB2 → EPUB (built in) → AZW3 (calibre). Its edges cost 20, so
built-in routes always win where they exist.

calibre also makes PDFs of EPUB, MOBI, AZW3, AZW, PRC, FB2, HTMLZ, TXTZ and the formats only it reads (one
`ebook-convert` run: A4 with `--paper-size a4`, page numbers with `--pdf-page-numbers`, calibre's margins and font
sizes; the built-in route ignores the preset's PDF options too). For long books it is much faster than the built-in
route, HWPX writer → rhwp (a 640-page novel: about a minute against some 48 minutes, #127), but the built-in route
stays the default so results don't change under users. The PDF edges cost 23: in the default engine order the
built-in route costs 31 (HWPX writer 10 + 8, rhwp 10 + 3) and calibre 23 + 9, so the built-in route wins by one; once
the user moves calibre above the HWPX writer in Settings → *Engines*, calibre costs at most 31 and the built-in route
at least 32. LIT, LRF and the other formats only calibre reads go straight to PDF instead of calibre → EPUB → HWPX
writer → rhwp. When rhwp runs out of time and calibre is installed, the error says to rank calibre higher; Filee
never switches engines by itself.

- The pinned `calibre-64bit-<version>.msi` from download.calibre-ebook.com is unpacked like LibreOffice; the folder
  with `ebook-convert.exe` becomes `engines/calibre`.
- On macOS the pinned calibre needs macOS 14 (`minimumMacOS` in engines.json). Filee runs on macOS 13, where the
  download isn't offered; Settings → *Engines* points to calibre 7.26, the last version for macOS 13, as the user's
  own copy.
- Each run gets its own configuration, cache and temp folders in the job's work directory
  (`CALIBRE_CONFIG_DIRECTORY`, ...), so a calibre the user installed is never read or changed; messages are
  English (`CALIBRE_OVERRIDE_LANG`) and progress comes from its "34% ..." lines.

## HWP ↔ HWPX

HWP → HWPX is built in (the [HWP reader](#hwp-reader) writes the package from the records); rhwp's `export-hwpx`
remains as a route ranked below it. HWPX → HWP is rhwp's `convert`: anything → HWP goes through the HWPX writer and
then rhwp.

For HWP / HWPX → PDF, rhwp renders a copy of the HWPX (an HWP is exported with `export-hwpx` first) when Filee has to
change something for it (`Hwp/RhwpInput.cs`): fonts that are installed, the laid-out size of boxes sized relative to
the column, spaced equation commands, and, in an HWPX exported from an HWP (rhwp's `META-INF/rhwp-hwp5-origin`
marker), the stored line of a cell paragraph that holds only a floating table moved up to the paragraph's top, so the
table is not drawn over the paragraphs below it (#49). Paragraphs without stored lines (Word equations written by the
HWPX writer) get room for their equations: rhwp puts a line's baseline at 85 % of its tallest equation and the
equation's own baseline on it, so a fraction, matrix or sum hangs below the line; that overhang is added as space
after the paragraph (#143).

An HWPX with text that needs shaping (Arabic, Hindi, Thai, ...) goes to LibreOffice with H2Orestart instead, like the
documents above; an HWP is not scanned and stays with rhwp.

rhwp renders text slowly: about 1 s per KB of section XML, some 4 s per page of a novel (rhwp 0.8.7 on an
i7-1185G7, pictures cost little). So each rhwp run's time limit follows the document's text: 2 minutes + 30 minutes
per MB of unpacked `Contents/section*.xml` (an HWP counts three times its file size, its text being deflated), at most
60 minutes, after which a rhwp that hangs is stopped. The progress estimate assumes 15 minutes per MB. Cancelling
stops rhwp at once. A time-out fails with "rhwp couldn't finish this long document in time" (with calibre installed,
it suggests ranking calibre above the HWPX writer for e-books). The other engines keep fixed limits: LibreOffice 3
minutes, Ghostscript 5, Pandoc 2 and calibre 10 (it made the 640-page novel's PDF in a minute).

## Archives

The archive engine (`Archives/ArchiveConverter.cs`, id `archive`) uses Filee's own copy of the 7-Zip console program
(`7z.exe` + `7z.dll`, taken from the official x64 MSI with `msiexec /a` by `build/fetch-engines.ps1 -Only 7zip` and
bundled with the installer), never a 7-Zip installed on the PC. It runs as a separate process (LGPL).

| Format | Read | Write |
|---|---|---|
| ZIP, JAR, WBPZ | 7-Zip | ZIP in-process (System.IO.Compression): UTF-8 names with the language-encoding flag, times, empty folders |
| 7Z | 7-Zip | 7-Zip |
| TAR | 7-Zip | in-process (System.Formats.Tar, PAX: UTF-8 names, times) |
| TAR.GZ | 7-Zip, in two stages | in-process (PAX TAR + GZipStream) |
| TAR.BZ2, TAR.XZ | 7-Zip, in two stages | TAR in-process, compressed by 7-Zip |
| TAR.7Z, TAR.Z | 7-Zip, in two stages | — |
| LZ, TAR.LZ (lzip) | Filee: each member rewritten as a .lzma file for 7-Zip's LZMA decoder, CRC and size checked | — |
| GZ, BZ2, XZ | 7-Zip | 7-Zip; one file only (from another single-file format, or "Compress into one archive" with one file) |
| LZMA, Z | 7-Zip | — |
| RAR 4/5, CAB, CPIO, DEB, RPM, DMG, ISO, IMG, LHA/LZH, ARJ | 7-Zip (an RPM's payload is unpacked until the files appear) | — |
| Split archives (.001, .7z.001, .zip.001, .tar.001) | 7-Zip (combining .002, .003 … automatically) | — |
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
- When LibreOffice can't load an Office Open XML or OpenDocument file ("source file could not be loaded"), Filee
  converts once more from a copy in the job folder with the same parts in a plainly written ZIP (`PackageRepacker`):
  LibreOffice 26.2 refuses some valid containers, e.g. uncompressed entries with data descriptors (Tencent Docs, #141).
- It is the only reader of rarer formats, converted to PDF and to the editable formats of their kind: DOT, Works /
  WPS Writer, WordPerfect, Lotus Word Pro, AbiWord, Apple Pages, StarWriter, ClarisWorks / AppleWorks (`.cwk`),
  MacWrite (`.mcw`), T602 (`.602`) and Pocket Word (`.psw`) (→ DOCX, ODT, FODT, OTT, DOC, RTF, TXT, HTML); WPS
  Spreadsheets (ET), Apple Numbers, StarCalc and flat ODS (`.fods`) (→ XLSX, ODS, OTS, XLS, XLT, CSV, HTML); Apple
  Keynote, WPS Presentation (DPS), StarImpress / StarDraw and flat ODP (`.fodp`) (→ PPTX, ODP, OTP, PPT);
  Publisher, CorelDRAW, Visio, CGM, ODG and flat ODG (`.fodg`) (→ ODG, SVG, PNG, EMF, WMF). The `--convert-to`
  filter follows the application that opens the file (checked against LibreOffice's filter registry): Publisher,
  CorelDRAW and Visio open in Draw, CGM in Impress, and "SDA" files in Draw (.sda) or Impress (.sdd). ClarisWorks
  files can also be spreadsheets or drawings; Filee treats them as text documents, the common case. SVG, PNG, EMF
  and WMF export the first page.
- Written only by LibreOffice: FODT (`OpenDocument Text Flat XML`), the OpenDocument templates OTT / OTS / OTP
  (`writer8_template`, `calc8_template`, `impress8_template`) and XLT (`MS Excel 97 Vorlage/Template`). XLT is read
  by the built-in spreadsheet engine like XLS.
- SVG opens in Draw only for EMF, WMF and ODG; the vector engine keeps SVG → PDF and images. PDF opens in Draw
  (`draw_pdf_import`) only for SVG and ODG: Draw's SVG export draws every page of a PDF on top of each other, so Filee
  first copies the first page of the preset's page range into a PDF of its own (PDFsharp). EPS and PS reach SVG
  through Ghostscript's PDF; Draw would only show their preview picture.
- Checked with LibreOffice 26.2.6 (`LibreOfficeFormatTests`): FODT, FODS, FODP, FODG, T602 and SVG inputs, the
  template and FODT outputs, XLT read back by the spreadsheet engine, EMF / WMF headers and the one-page PDF → SVG.
  ClarisWorks, MacWrite and Pocket Word are binary formats without sample files in the repository: only their
  filters (`ClarisWorks`, `MacWrite`, `PocketWord File`, all opening in Writer) were checked in the registry.

## Pandoc

`Office/PandocConverter.cs` runs Pandoc for the markup languages Filee doesn't read itself: reStructuredText,
LaTeX, Org (`.org`), AsciiDoc (`.adoc`, `.asciidoc`), Typst (`.typ`), MediaWiki (`.mediawiki`, `.wiki`),
DocBook (`.dbk`, `.docbook`; written as DocBook 5), Jupyter notebooks (`.ipynb`), Textile (`.textile`), man pages
(`.man`) and OPML (`.opml`). Pandoc 3.12 reads and writes all of them.

- Each converts to and from Markdown, HTML, DOCX and ODT and into each other, and to RTF, EPUB and TXT. Through
  Pandoc's JSON AST they are also read by the HWPX writer, so they reach HWPX, HWP and PDF (HWPX writer → rhwp).
  There is no PDF through Pandoc itself (that would need a TeX or Typst engine).
- Edges into a markup language cost 11, one more than into Markdown, so a route with a Pandoc step in the middle
  (an e-mail's HTML → ? → RTF) stays on Markdown.
- Markdown, reStructuredText, Org, AsciiDoc, Typst, MediaWiki, Textile and notebooks → **PPTX** with Pandoc's
  PowerPoint writer (a slide per heading). These edges cost 20: with LibreOffice's cheap PPTX → PDF, Markdown → PPTX
  → PDF would otherwise beat Markdown → HWPX → PDF.
- Extensions that mean other things are not claimed: man pages only as `.man` (`.1` … `.9` are also rotated logs,
  `app.log.1`), DocBook as `.dbk` / `.docbook`, never every `.xml`.
- DocBook, man pages, OPML, Typst, HTML and LaTeX are written with `--standalone` (a root element, a `.TH` header,
  a preamble), the text formats extract pictures into `<name>_files` next to the result (Markdown with *Leave out
  pictures* gets none: a Lua filter drops them).
- Pictures are linked relative to the result (`<name>_files/media/…`; Pandoc runs in the result's folder, with
  `--resource-path` pointing at the source's for pictures the source links to), so both can be moved together.
  EMF and WMF pictures, common in Word documents, become PNG (drawn by the [metafile renderer](#emf--wmf-renderer);
  one that can't be drawn stays).
- Markdown is GitHub-flavoured and kept free of HTML, which many Markdown editors show as code (#391). A Lua filter
  lays every table out as a pipe table: a merged cell keeps its text in its first position and leaves the cells it
  covered empty, the paragraphs and list items of a cell are joined with `<br>`, and a table without a header row
  gets its first row as the header. Pictures lose their Word size, which only an `<img>` tag could hold.

## Subtitles

`Subtitles/SubtitleConverter.cs` (engine id `subtitle`) converts text subtitles in managed code, no FFmpeg needed.
Every reader produces one cue model (`SubtitleModel.cs`: start, end and text with line breaks and SRT's own tags
`<i>`, `<b>`, `<u>`, `<font color>`), every writer takes it, so any format converts to any other and to itself (an
SRT or SAMI file in CP949 comes out as UTF-8).

| Format | Read | Written |
|---|---|---|
| SRT | numbers optional, blank lines inside cues and missing between them, `,` or `.` before the milliseconds, `{\an8}` codes dropped | UTF-8 with BOM, CRLF, tags kept |
| ASS / SSA | `Dialogue:` lines in the order of the `Format:` line; `\i \b \u \c`, bold/italic/underline styles, `\N`; drawings (`\p1`) and comments left out, shadow layers merged | ASS v4.00+ for 1920 × 1080, one "Default" style, override codes (SSA is read only) |
| WebVTT | NOTE / STYLE / REGION blocks skipped, cue settings ignored, `<i> <b> <u>`, the colour classes (`<c.yellow>`), entities; voices and timestamps dropped | UTF-8, the basic colour classes, text escaped |
| SAMI (SMI) | hand-made HTML: unquoted attributes, unclosed `<SYNC>` / `<P>`, `&nbsp` with or without `;`; of several languages (KRCC, ENCC, …) the one with the most lines | one class from the text's language (KRCC for Korean), `&nbsp;` where the screen goes blank, UTF-8 with BOM |
| LRC | `[mm:ss.xx]` (several per line), `[ti:]`, `[offset:]`, enhanced word times dropped; a line lasts until the next one | plain text, lines joined, an empty time line where the screen goes blank |
| TTML / DFXP | TTML 1 and the 2006 DFXP namespace; times relative to their parents; clock, frames (`ttp:frameRate`), ticks and offset times; spans and referenced styles | TTML 1 with a bottom region and tts: spans |
| MicroDVD (.sub) | `{frame}{frame}text` with the rate of a `{1}{1}25` first line (else 23.976), `\|` between lines, `{y:i}` / `{c:$BBGGRR}`; VobSub `.sub` (pictures) and SubViewer fail with a clear message | 23.976 fps, first line states it, plain text, `{Y:i}` for all-italic cues |
| SCC | CEA-608 caption channel 1: pop-on, paint-on and roll-up captions, row preambles, italics, special and extended characters, doubled control codes, drop-frame time codes | — |

- **Encodings**: a byte order mark (UTF-8, UTF-16) or valid UTF-8 decide first, then a `charset=` / `encoding=` the
  file declares. Otherwise CP949 when the bytes are CP949 pairs, most of them Hangul syllables of KS X 1001 and hardly
  any with a lead byte above C8 (Chinese GBK text uses those for many common characters), then the PC's code page on
  Chinese or Japanese Windows, else the Western one (Windows-1252 on Korean Windows). Everything is written as UTF-8.
- Cues without an end (LRC, the last SAMI line, an unterminated SCC caption) last 4 seconds; empty cues are dropped.
- Not kept: positions and alignment, fonts and sizes, karaoke timing, ASS animations, WebVTT regions and voices.
- Tests (`SubtitleTests.cs`) write every input in code, CP949 and GBK bytes included.

## DjVu

Not supported: DjVuLibre (GPL) has no pinned, redistributable Windows build in an archive the installer can unpack.
The official Windows release is an NSIS installer (`DjVuLibre-3.5.29_DjView-4.12_Setup.exe` on SourceForge), which
would have to be run (a system install) or unpacked with 7-Zip; MSYS2's packages are `.pkg.tar.zst` archives with
five dependency packages. A DjVu engine needs a zip of `ddjvu.exe` / `djvutxt.exe` and their DLLs published for
Filee (with the GPL source offer), then an `engines.json` component and an optional package.

## FFmpeg

`Media/FfmpegConverter.cs` converts every video format to every video format, to animated GIF or APNG, to a still PNG
frame or a folder of every frame, to every audio format (the sound track) and to SRT, ASS and WebVTT (the subtitles);
every audio format to every audio format; and GIF, APNG and PNG to MP4, WEBM and MOV.
Same-format edges exist too, so "MP4 720p" works on MP4 files and "MP3 128k" on MP3 files.
Raw H.265 and H.264 camera streams (`.h265`, `.265`, `.hevc`, `.h264`, `.264`, read only, #301) are Annex B elementary
streams without a container: FFmpeg reports them as format `hevc` / `h264` (`MediaInfo.RawStream`). They are always
encoded, never copied, because their frames carry no timestamps (a copy into MP4 had non-monotonic DTS). A stream
without timing information is read at FFmpeg's default 25 fps, so a camera that recorded at another rate plays faster
or slower; its frames are all kept.
CRI USM (`.usm`, read only, #485), the video of many games' cutscenes, is read by FFmpeg's `usm` demuxer and goes to
every video and audio target like the other read-only containers. Encrypted USM files (common in recent games) can't
be read and fail with FFmpeg's error.
A self-contained `.m4s` (read only, #354), a fragmented MP4 that starts with `ftyp` + `moov` and then holds
`styp` / `sidx` / `moof` / `mdat` fragments (GPAC and some downloaders save them), is read by FFmpeg's mp4 demuxer
and goes to every video and audio target. A DASH media segment, whose top-level boxes reach a `moof` before any
`moov`, plays only with a separate init segment; Filee refuses it before FFmpeg runs, with its own message
(`error.m4s_needs_init`), instead of FFmpeg's demuxer error.
Dahua DVR / NVR recordings (`.dav`, read only, #594) are read by FFmpeg's `dhav` demuxer, which finds them by the
`DHAV` chunks they are made of, and go to every video and audio target. Their H.264 / H.265 video is copied where
the target and preset allow it (`CanCopyVideo`), like any other container's; G.711 sound is encoded for targets that
can't hold it. The chunks carry the camera's wall-clock time, so FFmpeg reports the recording date as the start time;
the output starts at zero.
Bink videos (`.bik`, read only, #751), the cutscene format of many games, are read by FFmpeg's `bink` demuxer and
decoder and go to every video and audio target. FFmpeg has no Bink encoder, so nothing writes `.bik`.
A Video CD's `MPEGAV/AVSEQ01.DAT` (MPEG-1 in a RIFF/CDXA wrapper) counts as MPEG: `.dat` is an extension of that
format, and FFmpeg reads what the file holds, so a `.dat` with no video in it fails instead of making a file.

- The download is the gyan.dev **9.0.2 "full_build-shared"** Windows build (GPL-3.0, run as a separate program).
  It is smaller than the "essentials" build (100 vs 115 MB download, 272 vs 329 MB on disk, because ffmpeg.exe and
  ffprobe.exe share their libraries) and has a superset of its encoders. It links only Windows system DLLs; GPU
  and Vulkan libraries are loaded only if present. Being over 60 MB, it is offered but not ticked on first run.
- The Cut a clip window's preview plays its sound through `ffplay` when it sits next to Filee's FFmpeg (the gyan.dev
  build has it); without it, the preview plays silently. Nothing else uses ffplay.
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
  | GIF | two passes: palettegen over the whole clip, then paletteuse; 15/12/10 fps and at most 640/480/320 px wide (High/Balanced/Small); *Playback speed* adds `setpts=PTS/<speed>` before the frame rate |
  | APNG | one pass with the `apng` encoder (24-bit RGB, mixed prediction) and muxer, looping; the same frame rates, widths and playback speed as GIF |
  | MP4, M4V, MKV with *Video codec* HEVC · MOV with HEVC | x265 (preset medium, `hvc1` tag in MP4/M4V/MOV) + AAC |
  | MP4, M4V, MKV, WEBM with *Video codec* AV1 | SVT-AV1 (preset 8), or libaom-av1 (cpu-used 6, row multithreading) with an FFmpeg that lacks SVT-AV1 |
  | MP3 · M4A/M4B · AAC · OGG · OPUS · WEBA · MKA · WMA · MP2 · AC3 · E-AC3 · AMR | LAME VBR (V0/V2/V5) or CBR · AAC, or ALAC with *Audio codec* ALAC · AAC (ADTS) · Vorbis · Opus · Opus · Opus · WMA 2 · MP2 · AC-3 · E-AC-3 · AMR-NB 12.2k (8 kHz mono) |
  | WAV · W64 · AIFF · AU · CAF · VOC · FLAC · WV · TTA | 16-bit PCM (WAV and W64: 8/16/24/32-bit or 32-bit float; WAV switches to RF64 over 4 GB, Sony Wave64 has no limit) · FLAC · WavPack (lossless) · TTA |
  | SRT · ASS · VTT (from video) | the video's first text subtitle track, copied when it already is that format, else converted by FFmpeg |

  Read only (FFmpeg's decoders): APE (Monkey's Audio), Musepack (MPC), TAK, DSF and DFF (DSD), DTS, Speex (SPX) and the
  formats above. No encoder for them is in the build, or (DTS) only an experimental one.
  Apple Voice Memos recordings (`.qta`, read only, #646) are QuickTime audio: FFmpeg's mov demuxer reads them and
  they go to every audio target, from the first audio track FFmpeg can decode, like any other source
  (iOS 26 memos also carry a spatial APAC track FFmpeg has no decoder for).

- **Rewrapping instead of re-encoding.** When the target container takes the source's codecs as they are
  (`MediaEncoding.Remuxable`: H.264/HEVC + AAC, ALAC, AC-3, E-AC-3 or MP3 for MP4/M4V/MOV, also ProRes and PCM for
  MOV; H.264/HEVC/VP9/AV1 with most audio for MKV; VP8/VP9/AV1 + Opus/Vorbis for WebM; H.264/HEVC for TS/M2TS),
  the streams are copied (`-c copy`). An iPhone MOV becomes an MP4 in seconds, frame for frame, with 10-bit HDR
  (HLG, Dolby Vision's HLG base), rotation and colour tags kept. HEVC gets the `hvc1` tag Apple players need. Video
  is copied only in a pixel format players decode (4:2:0, 8 or 10 bits; ProRes as it is) and when no scaling down
  is needed; audio only when no bitrate or other channel count is set. Quality *Small* always re-encodes (the user
  asked for a smaller file); *High* and *Balanced* only set how a stream that has to be encoded is encoded. A
  stream that can't be copied is encoded as below, the other one is still copied.
- Preset options (`MediaOptions`): *Quality* picks CRF 18/23/28 (x264), 22/26/30 (x265), 26/32/40 (SVT-AV1 and
  libaom), 24/32/38 (VP9), quantizer 2/4/7 (MPEG-4, WMV, MPEG-2), Theora 8/6/4 and the audio bitrates (e.g. AAC and
  E-AC-3 256/192/128 kbit/s per stereo pair); *MaxHeight* scales down only, keeps the aspect ratio and even sizes;
  *AudioBitrateKbps* overrides the audio bitrate (snapped to the MP3/MP2/AC-3 bitrate tables; the editor offers 32 to
  320 kbit/s); *RemoveAudio* drops the sound; *AudioChannels* (0 source, 1 mono, 2 stereo) applies to every audio
  target but AMR; *AudioSampleRate* (8 to 48 kHz) and *WavFormat* (bit depth) apply to WAV and W64.
- *VideoCodec* (Default, HEVC, AV1) and *AudioCodec* (Default, ALAC) pick another codec where the target takes it
  (`MediaCodecs` in Filee.Core: HEVC for MP4/M4V/MKV/MOV, AV1 for MP4/M4V/MKV/WEBM, ALAC for M4A/M4B); for any other
  target the usual codec is written. The presets "MP4 HEVC" (on the video donut), "MP4 AV1" and "ALAC" use them. A
  video already in the chosen codec is copied (the rules above, with AV1 also into MP4); HEVC and AV1 keep 10 bits
  (`yuv420p10le`, Main 10) for 10-bit and HDR sources and write their colour tags (BT.2020, PQ or HLG), 8-bit 4:2:0
  for the rest. The encoders are looked up with `ffmpeg -encoders` once per FFmpeg, so a copy the user picked without
  x265 or AV1 fails with a clear message instead of an FFmpeg error. x265 and SVT-AV1 log only errors
  (`-x265-params log-level=error`, `SVT_LOG=1`).
- **Hardware encoding** (*Hardware acceleration* in the video card, `MediaOptions.HardwareEncoding`; off by default and
  for older presets, so nobody's results change without asking). H.264, HEVC and AV1 encodes (not VP9 and the
  MPEG-style codecs, not stream copies, not pictures → video) go to the graphics chip (`Media/HardwareVideo.cs`,
  `Media/HardwareProbe.cs`):

  | System | Tried in this order |
  |---|---|
  | Windows | `*_nvenc` (NVIDIA), `*_qsv` (Intel Quick Sync), `*_amf` (AMD) |
  | macOS | `*_videotoolbox` with constant quality (Apple Silicon), then with a bitrate (Intel Macs reject `-q:v`); none for AV1 (VideoToolbox has no AV1 encoder) |
  | Linux | `*_nvenc`, then `*_vaapi` on `/dev/dri/renderD128` (`-vaapi_device`, frames uploaded with `format=nv12,hwupload`) |

  AV1 encodes only on recent chips: NVIDIA RTX 40 and newer (`av1_nvenc`), Intel Arc and Core Ultra (Meteor Lake) and
  newer (`av1_qsv`), AMD RDNA 3 and newer (`av1_amf`), and VA-API drivers that offer it; on older ones the test
  encode fails and SVT-AV1 encodes as before. FFmpeg lists encoders the computer can't run (NVENC without an NVIDIA
  card), so only the ones `ffmpeg -encoders` lists are tried, each first with a test encode with the real options
  (0.1 s of `testsrc` at 256 × 144 to the null muxer, 15 s limit). The first that succeeds is used; results are kept
  per FFmpeg executable, encoder, rate control and bit depth until Filee restarts, and written to the log
  ("Hardware encoder av1_qsv (Quality, 8-bit) works" / "is not usable: …"). Quality: *High/Balanced/Small* become
  quantizer 19/24/29 for H.264 and 23/27/31 for HEVC (one above x264's and x265's CRF: hardware encoders spend bits
  less wisely) as NVENC `-rc vbr -cq N -b:v 0 -preset p5`, Quick Sync `-global_quality N -preset medium` (ICQ), AMF
  `-rc cqp -qp_p N -qp_i N−2` (`-quality balanced`) and VA-API `-rc_mode CQP -qp N`; VideoToolbox gets `-q:v`
  70/60/50, or on Intel Macs `-b:v` from 0.14/0.09/0.05 bits per pixel per frame (× 0.65 for HEVC). AV1 has other
  scales: NVENC's `-cq` is AV1's 0–63 quantizer and gets 28/34/42 (two above SVT-AV1's CRF 26/32/40); AMF and VA-API
  take the 0–255 quantizer index, four times that (112/136/168: AMF `-qp_p`/`-qp_b` N, `-qp_i` N−8; VA-API
  `-rc_mode CQP -global_quality N`, as `av1_vaapi` has no `-qp`); Quick Sync's ICQ is 1–51 for AV1 too and gets
  HEVC's 23/27/31. 8-bit video is NV12 (H.264 High, HEVC Main, AV1 Main); 10-bit and HDR sources stay P010 (HEVC Main
  10, 10-bit AV1 Main) with their colour tags when the encoder passes a 10-bit test encode, otherwise x265 / SVT-AV1
  encode them. AV1 gets no `-profile:v` (every encoder picks Main; `av1_nvenc` has no such option) and no tag (`av01`
  in MP4/M4V, as written to MKV and WebM). HEVC keeps the `hvc1` tag in MP4/M4V/MOV. GOP length and B-frames are
  left to each encoder's defaults, as for H.264 and HEVC. When a hardware encode fails anyway (a picture too large for
  the chip, a driver error), the partial output is deleted and the file is encoded again with x264/x265/SVT-AV1; that
  is logged, not shown as a failure.
- DSD (DSF, DFF) decodes at 2.8 MHz or more: lossless and PCM targets get 88.2 kHz, Vorbis and WMA 44.1 kHz, the
  other lossy encoders their nearest supported rate.
- Subtitle extraction takes the first text subtitle track (SubRip, ASS/SSA, WebVTT, MP4 timed text, CEA-608, …).
  A video without subtitles fails with "The video has no subtitle track."; one with only picture subtitles (Blu-ray
  PGS, DVD, DVB) says they can't be turned into text without OCR. Video → SAMI, LRC, TTML, … goes through SRT and the
  subtitle engine (ASS and WebVTT extraction cost 11 so SRT is the hub).
- The first video stream (cover pictures excluded) is converted. Video to video keeps every audio track (languages,
  commentary), each copied where the target takes its codec or encoded on its own, with its language tag; FLV and DV
  hold one and keep the first. Video to audio takes the first. Tracks FFmpeg can't decode (no decoder, like the spatial
  APAC track of iPhone recordings, or a codec ffprobe can't name) are skipped, unless the file has no other sound; the
  clip window's waveform, *Remove silence* and clip music take the same track. Files without sound still convert to video. Title, artist
  and other tags and chapters are kept, album art too for MP3, M4A, M4B and FLAC. Portrait phone videos are turned
  upright. Odd sizes are evened out, frame rates that MPEG-2/MXF/MPEG-4 part 2 cannot store are converted, and channels
  WMA or AMR cannot hold are mixed down.
- Progress comes from `-progress` (position ÷ duration from ffprobe); the GIF palette pass reports per-frame stats of
  a discarded copy of its frames. Cancelling kills the process tree, waits (up to 10 seconds) until it has exited and
  deletes the partial output. There is no time limit, but FFmpeg is stopped when it reports nothing for 10 minutes.
  At most two conversions run at a time (FFmpeg's encoders use every core themselves).
- Video → PNG writes one still frame (a tenth into the video, where the picture has usually faded in); the image
  engines turn it into JPG, WEBP, PDF, …. The GIF edge costs more, so these routes never go through an animated GIF
  of the whole video.
- **Every frame** (*Save every frame of a video* on a PNG or JPG preset, `MediaOptions.SaveEveryFrame`; the presets
  "Extract frames (PNG)" and "Extract frames (JPG)", on no donut): `Media/FrameSequenceWriter.cs` writes
  `frame_000001.png`, `frame_000002.png`, … into one folder, named by the output rule with `_frames` added
  (`clip_frames`, `clip_frames (2)`; skipped, or filled over like an extracted archive, by the conflict rule).
  - Routes: the frame edges (`ConversionEdge.FrameSequence`, video → PNG and video → JPG) are taken only when
    `Preset.SavesFramesOf` the file (the option is set, the target is PNG or JPG and the file is a video), and then as
    the whole route (`RoutePlanner.Plan(from, to, frames: true)`). Without the option video → JPG stays the still frame
    above, which the image engines turn, resize and so on; other files and targets ignore the option.
  - *Every Nth frame* (1–1000) keeps frames 1, N + 1, 2N + 1, … (`select`, `-fps_mode vfr`). Otherwise every decoded
    frame is written once (`-fps_mode passthrough`): a variable frame rate phone video gives as many pictures as it
    has frames, not one per 1/30 s with the slow parts repeated. The pictures are numbered, not named by time.
  - Only the first video stream is read (`-map 0:V:0`). Pixels are made square and portrait videos are turned
    upright, as for the still frame; nothing else of the image options is applied (no resizing, rotating or
    metadata). JPG uses the preset's quality (100 → `-q:v 2`, 90 → 5, 1 → 31). HDR video (PQ, HLG) is not
    tone-mapped, so its frames look flat and grey; 10-bit video becomes 16-bit PNG.
  - Before anything is written the pictures are estimated (duration × frame rate ÷ interval). More than 100,000
    (`MediaOptions.MaxFrames`, about an hour at 30 fps), or more than the free space of the output drive (estimated at
    1.5 bytes a pixel for PNG, 3 for 10-bit video, 0.25 for JPG), fails with a reason in the user's language
    (`ConversionRefusedException`); a file that doesn't say its length or frame rate is not checked. The editor shows
    how many pictures a minute of 30 fps video gives at the chosen interval.
  - The frames go into a hidden folder next to the output and are moved into place when FFmpeg has finished, so a
    cancelled or failed run leaves no half-filled folder.
- **Live / Motion Photos** (*Extract Live/Motion Photo video* on an MP4 or MOV preset, `MediaOptions.ExtractMotionPhoto`;
  the preset "Extract Live Photo video", on no donut): the video saved with a phone photo becomes the output, with its
  sound, instead of a video of the still picture.
  - Routes: the motion edges (`ConversionEdge.MotionPhoto`, JPG, HEIC, AVIF and LIVP → MP4 and MOV,
    `MediaOptions.MotionPhotoSources`) are taken only when `Preset.ExtractsMotionOf` the file, and then as the whole
    route (`RoutePlanner.Plan(from, to, motionPhoto: true)`). A LIVP always gives its video; GIF, PNG, WebP and other
    pictures ignore the option and become a video of the picture as below.
  - Where the video is (`Media/MotionPhoto.cs`, `Media/LivePhotoSource.cs`), always from the photo's own metadata,
    never by searching its bytes for an MP4 signature: Motion Photo 1.0 JPEGs (Pixel, Samsung, …) give the length of
    the appended video in the XMP container directory (`GCamera:MotionPhoto`, the last `Container:Item`, attributes or
    elements); older Google "MicroVideo" JPEGs an offset back from the end (`GCamera:MicroVideoOffset`); HEIC and AVIF
    hold the video in a top-level `mpvd` box. The video must start with `ftyp` and hold a `moov`, else the metadata is
    stale (an editor rewrote the picture) and nothing is guessed; it ends with its last whole box, which leaves
    Samsung's SEF trailer out. An iPhone photo (JPG or HEIC) takes the MOV next to it with the same name, only when
    both carry the same content identifier (tag 17 of Apple's maker note, `com.apple.quicktime.content.identifier`);
    a LIVP (the ZIP iCloud downloads give) takes its one MOV, whose identifier is only checked for the log.
  - The video is copied into the work directory (at most 512 MB; JPEG metadata is read only from the first 8 MB, XMP
    without DTDs; LIVP entries are copied to names of Filee's own, never extracted by their names), converted there
    like any video (copied as it is into MP4/MOV when its codecs fit) and moved into place when finished: a failed or
    cancelled run never touches a file "overwrite" would replace, and the output never replaces the iPhone MOV it was
    read from (IMG_0001.HEIC → `IMG_0001 (2).mov`). A photo without a video fails with "This photo has no video
    Filee can read"; why goes to the log.
  - Not read: vendor formats without this metadata (Huawei's "LIVE_" trailer, for one).
- **Making Live / Motion Photos** (experimental until confirmed on phones; *A video becomes* on a JPG or HEIC preset,
  `MediaOptions.LivePhoto`; the presets "Live Photo (iPhone)" and "Motion Photo (Android)", JPG, on the back of the
  video donut, added in settings schema 31): a short clip of a video becomes a moving photo instead of one still
  frame.
  - Routes: the live edges (`ConversionEdge.LivePhoto`, every video → JPG and HEIC) are taken only when
    `Preset.MakesLivePhotoOf` the file (an iPhone Live Photo as JPG or HEIC, a Motion Photo as JPG,
    `MediaOptions.LivePhotoTargets`), and then as the whole route (`RoutePlanner.Plan(from, to, livePhoto: true)`); "Save
    every frame" is ignored meanwhile. Other files and targets convert as usual.
  - The clip (`Media/LivePhotoWriter.cs`): *Clip start*, *Clip length* (0.5 to 5 s, 3 by default, shorter when the
    video ends sooner; a start past the end is refused) and *Photo at* (seconds into the clip, 1.5 by default, the last
    frame at most). FFmpeg encodes it at 30 fps, upright (the phone's rotation is applied to the pixels), scaled to at
    most 1440 px on the shorter side (or the preset's maximum height), 8-bit 4:2:0 H.264 (HEVC when the preset asks,
    tagged `hvc1`), the sound as AAC (mono or stereo, 44.1 or 48 kHz) unless the preset removes it. HDR (PQ or HLG) is
    tone-mapped to SDR BT.709 with zscale; an FFmpeg without zscale refuses HDR with a message. The photo is the same
    frame at the video's full size, picked by the same 30 fps timing (`select` on the frame number the clip really has,
    counted from its `stsz`), encoded at the preset's image quality.
  - **iPhone Live Photo**: a JPG (or HEIC) and a MOV with the same name and one content identifier (a new upper-case
    UUID), the pair Photos imports as a Live Photo. The photo carries it in Apple's maker note (Exif `MakerNote`,
    "Apple iOS" big-endian, tag 17, ASCII), HEIC through the HEIC writer's Exif item. The MOV (QuickTime, `qt  ` brand)
    gets, as an iPhone writes them: `com.apple.quicktime.content.identifier` in a movie-level `meta` (plain container,
    `mdta` handler, `keys`, `ilst` with a UTF-8 `data` item), and a timed metadata track for the still: track flags 0xF,
    `tref/cdsc` to the video track, a `mhlr/meta` handler, `gmhd`, an alias data reference, a `mebx` sample entry whose
    key 1 is `mdta` + `com.apple.quicktime.still-image-time` typed as a signed 8-bit integer (well-known type 65), one
    9-byte sample (the item, value -1) of one tick at the movie's timescale, placed by an empty edit at the photo's
    time (`AppleLivePhoto.TagMovie`). FFmpeg leaves the movie header at the end, so only that header is rewritten: the
    sample goes into a small `mdat` where it was, and no chunk offset moves. Not written: the other timed metadata of
    an iPhone (live-photo-info, the still's transform, video orientation) and its camera keys, which Photos does not
    need to pair the files.
  - **Android Motion Photo**: one `NAME.MP.jpg` (the name a Pixel gives them): the JPEG, then the clip as MP4 appended,
    ending the file. Its XMP APP1 (after the JFIF APP0 and an Exif APP1) is laid out as a Pixel's:
    `GCamera:MotionPhoto` 1, `MotionPhotoVersion` 1, `MotionPhotoPresentationTimestampUs` (the photo's time in the
    clip), and a `Container:Directory` of the primary JPEG and the video (`video/mp4`, `MotionPhoto`, its length,
    `Padding` 0); the older `GCamera:MicroVideo` properties (the same length as `MicroVideoOffset`) are written too for
    older Google Photos (`MotionPhoto.WriteAsync`). Samsung's own SEF trailer is not written.
  - Expected to work, to be confirmed on devices: Photos on iPhone, iPad and Mac with both files imported together
    (iCloud Photos, AirDrop of both, Image Capture or Photos on a Mac; a single file sent through a messenger loses the
    pairing), Google Photos on Android, iPhone and the web, and galleries that read Google's Motion Photo XMP (Samsung
    Gallery on recent One UI is likely but untested). Not expected: galleries that only know their maker's own format.
  - Everything is written in the work directory and moved into place at the end, first next to its target under a
    hidden name, then renamed over it: a failed or cancelled run never touches an existing file. The pair keeps one
    name: when the MOV's name is taken (another file, or the video being converted, which is never written over) both
    take the next number (`clip (2).jpg` + `clip (2).mov`); a MOV is only written over together with the photo
    "overwrite" replaces; "skip" skips the pair when the photo exists. Filee's own extraction (above) reads both kinds
    back.
- Pictures → MP4/WEBM/MOV go through a full-size PNG from the image engines (HEIC → PNG → MOV): a 5-second still at
  30 fps, one quality step above the preset (identical frames cost almost nothing; x264 `-tune stillimage`). The
  PNG's ICC profile is read (`-flags2 +icc_profiles`), so primaries and transfer are tagged as the photo's (Display
  P3 stays P3); RGB → BT.709 YUV in TV range; transparency is premultiplied onto black. The size is kept (made even)
  unless it exceeds H.264/VP9 level 6.2 (8192 × 4352 pixels, 16384 per side) or the preset's maximum height. The
  image engines' still picture → GIF and → APNG edges cost 12 (`ImageEncoder.Edge`) and PNG → video 11, so a photo
  takes the PNG route (21 < 22) while animated GIF/WebP/MNG, multi-page TIFF and PDF keep the GIF route (20): a moving
  video or a slideshow of the pages. APNG → video costs 11, so an APNG goes straight to video and nothing else detours
  through it.
- **Images → HEIC** (`Heic/HeicWriter.cs`): ImageMagick has no HEVC encoder and FFmpeg no HEIF muxer, so the work is
  split. ImageMagick reads any image it knows and applies the preset (orientation, resize, metadata; transparency is
  flattened onto the background colour, HEIC alpha needs an auxiliary picture Filee does not write) and writes an
  8-bit PNG. FFmpeg pads it to whole 512 × 512 tiles with its own edge pixels (`fillborders`), converts to BT.601
  full-range 4:2:0 like JPEG and cameras, cuts the tiles (`untile`) and has x265 encode each as an IDR picture of
  the Main profile (`-force_key_frames expr:1 -forced-idr 1`, `open-gop=0`; with `keyint=1` x265 would signal the
  "Main Intra" range extensions profile, which the `heic` brand does not allow and Windows decodes wrongly). Pictures
  up to 512 × 512 are one coded picture. Quality 1–100 maps to CRF 50 − 0.4 × quality (90 → 14, 80 → 18).
  `HevcStream` splits the Annex B output and builds the hvcC record; `HeifFile` writes ftyp `heic`/`mif1`, a grid
  item cropping the tiles to the picture size, hidden tile items, `ispe`/`pixi`, the ICC profile as `colr prof` (or
  `nclx` sRGB, BT.601 full range when there is none) and Exif (orientation reset to 1, thumbnail removed) as a `cdsc`
  item. Checked with libheif (Magick.NET), FFmpeg's HEIF demuxer and Windows' HEIF/HEVC extensions; a smooth 2000 ×
  1500 picture is a quarter the size of a JPG at the same PSNR.

## Text recognition (OCR)

"Recognize text" (`PdfOptions.Ocr`, issue #288) in a preset for PDF, DOCX, TXT, Markdown or HWPX reads the text of
scans and pictures with Tesseract (`Ocr/TesseractConverter.cs`), in the language the preset names
(`PdfOptions.OcrLanguage`, a Tesseract code; English by default). The built-in "Searchable PDF" preset turns it on.

- The route planner puts an OCR edge first (`ConversionEdge.Ocr`, `RoutePlanner.Plan(ocr: true)`): picture or PDF →
  searchable PDF, then the cheapest ordinary route from that PDF. A picture Tesseract can't open (HEIC, PSD, …)
  becomes a PNG first. OCR edges are never part of an ordinary route.
- A searchable PDF keeps the page as it looks and puts the recognized words on it as invisible text. PDF pages that
  already have text (visible or an OCR layer) are copied as they are. A scan with a stamp still counts as a scan: a
  picture over at least half the page whose only text is visible and covers under 1% of it (a page number or Bates
  number the scanner software added). Only the others are rendered at 300 dpi
  (PDFium) and recognized page by page: a page is rendered when one of at most four Tesseract runs (fewer on PCs with
  fewer cores) is free, each with one thread (`OMP_THREAD_LIMIT=1`), and its picture is deleted once its PDF exists,
  so a long scan never has more than four pictures in the work folder. A PDF whose
  pages all have text is copied with a note. The new document keeps the pages and the document information, not
  bookmarks or form fields.
- DOCX, Markdown and HWPX read the OCR layer as the page's text (`PdfDocumentReader.ReadWithOcrLayer`): the words
  become paragraphs, the scan behind them is left out, and Tesseract's placeholder font is dropped. Without OCR an
  OCR layer stays invisible there and the page stays a picture, as before. TXT always reads it.
- PDFs merged into one ("Merge PDF" with OCR) are recognized before they are merged.
- Pictures without a resolution are read at 300 dpi; Tesseract would otherwise assume 70 dpi and make a huge page.
- The language data is copied into the job's folder and passed with `--tessdata-dir`, so a system Tesseract reads
  only Filee's pinned files; `-c tessedit_create_pdf=1` replaces the `pdf` config file that lives in Tesseract's own
  tessdata folder.

### Apple Vision on macOS

On a Mac the same step runs with Apple Vision (`Ocr/VisionConverter.cs`, `VNRecognizeTextRequest` at the accurate
level and the newest revision the Mac has), part of macOS, so nothing is downloaded. It has the same OCR edges as
Tesseract and is registered before it, so the route planner takes it; on Windows and Linux it is unavailable
("Not available on this platform").

- The preset's language (a Tesseract code) is mapped to Vision's identifiers (`VisionLanguages`) and asked for with
  English next to it; if Vision refuses the pair it reads with the language alone. Which languages Vision reads comes
  from the Mac itself (`supportedRecognitionLanguagesAndReturnError:`, more with newer macOS versions); a language it
  doesn't read (Hindi, Nepali, Greek, …) goes to Tesseract when that is installed, and is refused otherwise with
  "Apple Vision can't read this language. Install Tesseract …". The preset editor shows no download size for the
  languages Vision reads, and its hint names Apple Vision.
- PDFs go page by page exactly like Tesseract's (`TesseractConverter.RecognizePdfAsync`, at most two pages at a time).
  A JPEG picture goes into the PDF as it is, other pictures (and a JPEG that EXIF turns) as an upright PNG; every page
  of a TIFF becomes a page. The page size comes from the picture's resolution, 300 dpi when it has none.
- Each page is the picture with the recognized words as invisible text (`OcrPdfWriter`, render mode 3): one font
  size per line (its height), the baseline at the line's bottom, each word stretched (`Tz`) to where the next one
  starts with its space, so search, selection and copying follow the picture. Vision gives a box per word
  (`boundingBoxForRange:error:`); without one the words are spread over the line by their length.
- The text is set in a TrueType font with only empty glyphs, built in code (`GlyphlessFont`): every UTF-16 unit is
  one character code (`Identity-H`), shown as glyph 1, and the `ToUnicode` map gives it back unchanged, so any
  script works without a font in the file. `PdfDocumentReader` and `PagesWithText` read the layer like Tesseract's.
- Vision is called through the Objective-C runtime (`Ocr/AppleVision.cs`), without a bridging library. A `CGRect`
  result needs `objc_msgSend_stret` on Intel Macs; objects made with `alloc`/`init` are released, the rest go with an
  autorelease pool per page. A cancelled job cancels the running request.
- Tests: `VisionLanguageTests`, `OcrTextLayerTests` and `VisionConverterTests` (a stand-in recognizer) run everywhere;
  `AppleVisionTests` read real pictures on macOS (CI's Apple silicon and Intel jobs).

## Whisper

Filee transcribes speech in video and audio files into SRT subtitle files using OpenAI Whisper (`whisper.cpp`).
Transcription runs entirely locally and offline on your computer; no audio or data is sent to external servers.

- **Platform**: Windows only (x64). On macOS and Linux, the Whisper package is not offered and the engine reports
  `engine.reason.unsupported_platform` ("Not available on this platform").
- **Download size**: ~157 MB total (~9 MB for `whisper-cli.exe` and ~148 MB for the default `ggml-base.bin` model; ~160 MB on disk).
  Downloads are pinned by SHA-256 in `engines.json`. Users can also place larger Whisper models (such as `ggml-small.bin`
  or `ggml-large-v3-turbo.bin`) into the engine directory or point to a custom installation in Settings → *Engines*.
- **FFmpeg requirement**: Whisper operates on 16 kHz mono 16-bit WAV audio. Extracting audio from arbitrary video and audio
  formats requires FFmpeg. FFmpeg must be installed (via Settings → *Engines*, bundled, or found on `PATH`).
- **Languages**: Whisper supports auto-detection (`auto`) as well as specific language codes (`-l <code>`, e.g. `hi` for
  Hindi, `en` for English, `es` for Spanish, `fr` for French, `ja` for Japanese, `zh` for Chinese).
