MathJax 3.2.2, Apache-2.0, from https://github.com/mathjax/MathJax/tree/3.2.2

`tex-svg-full.js` is the unmodified upstream browser bundle from
https://cdn.jsdelivr.net/npm/mathjax@3.2.2/es5/tex-svg-full.js

It is embedded in Filee.Engines.dll for Windows Markdown PDF rendering.
No CDN requests are made during conversion. The bundle includes TeX extensions
and SVG glyphs. Chromium/Edge/Chrome prints the resulting vectors after MathJax
and document fonts have finished loading.

The original Markdown HTML/TXT and other document conversion paths are retained.
The PDF path is available when Edge or Chrome is found in its standard Windows
installation directory.
