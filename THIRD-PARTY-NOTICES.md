# Third-party notices

Filee is MIT-licensed. It uses and redistributes the following third-party software.
Each component remains under its own license.

## Libraries (NuGet)

| Component | License | Project |
|---|---|---|
| Avalonia UI | MIT | https://github.com/AvaloniaUI/Avalonia |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| SharpHook (libuiohook) | MIT (libuiohook: GPL-3.0 with linking exception / LGPL-3.0) | https://github.com/TolikPylypchuk/SharpHook |
| Velopack | MIT | https://github.com/velopack/velopack |
| Magick.NET / ImageMagick | Apache-2.0 / ImageMagick License | https://github.com/dlemstra/Magick.NET |
| ImageMagick delegate libraries (libheif, libde265, libwebp, libjpeg-turbo, libpng, libtiff, zlib, …) | Various (LGPL-2.1+, BSD, zlib) | bundled inside Magick.NET |
| PDFsharp | MIT | https://github.com/empira/PDFsharp |
| PDFtoImage | MIT | https://github.com/sungaila/PDFtoImage |
| PDFium (bblanchon.PDFium) | BSD-3-Clause / Apache-2.0 | https://pdfium.googlesource.com/pdfium |
| SkiaSharp | MIT | https://github.com/mono/SkiaSharp |
| Svg.Skia (incl. Svg, ShimSkiaSharp, ExCSS; text shaping in `Filee.Engines/Vector/OutlinedTextPlayback.cs` adapted from it) | MIT | https://github.com/wieslawsoltes/Svg.Skia |
| HarfBuzzSharp | MIT | https://github.com/mono/SkiaSharp |
| SharpCompress (zstd decompression for engine downloads) | MIT | https://github.com/adamhathcock/sharpcompress |
| Unhwp | MIT | https://github.com/iyulab/unhwp |
| Markdig | BSD-2-Clause | https://github.com/xoofx/markdig |
| ExcelNumberFormat | MIT | https://github.com/andersnm/ExcelNumberFormat |
| pypandoc-hwpx (Pandoc AST mapping and package layout in `Filee.Engines/Hwp/Hwpx`, incl. the `blank.hwpx` reference document) | MIT, Copyright (c) 2024 pypandoc-hwpx Contributors | https://github.com/msjang/pypandoc-hwpx |
| Microsoft.Extensions.* | MIT | https://github.com/dotnet/runtime |

## Engines

Bundled with the installer:

| Component | License | Project |
|---|---|---|
| rhwp | MIT | https://github.com/edwardkim/rhwp |

Downloaded from their official release pages when the user chooses to install them
(pinned in `src/Filee.Engines/Infrastructure/engines.json`):

| Component | License | Project |
|---|---|---|
| LibreOffice | MPL-2.0 | https://www.libreoffice.org |
| H2Orestart (HWP/HWPX import for LibreOffice) | GPL-3.0 | https://github.com/ebandal/H2Orestart |
| Eclipse Temurin JRE (for H2Orestart) | GPL-2.0 with Classpath Exception | https://adoptium.net |
| Pandoc | GPL-2.0-or-later | https://github.com/jgm/pandoc |
| Ghostscript (conda-forge build) | AGPL-3.0 | https://www.ghostscript.com, https://github.com/conda-forge/ghostscript-feedstock |
| Microsoft Visual C++ Redistributable (for Ghostscript, conda-forge `vc14_runtime`) | Microsoft Visual C++ Redistributable license | https://github.com/conda-forge/vc-feedstock |

These programs run as separate processes. Their source code is available from the linked projects.

## Fonts and icons

| Component | License | Project |
|---|---|---|
| Noto Sans, Noto Sans KR, Noto Sans SC | SIL Open Font License 1.1 | https://github.com/notofonts |
| Material Design Icons (Pictogrammers) | Apache-2.0 | https://pictogrammers.com |
