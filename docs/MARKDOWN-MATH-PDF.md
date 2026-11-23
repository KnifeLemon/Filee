# Markdown equations in PDF (draft)

The HWPX Markdown reader represents `MathInline` and `MathBlock` as italic text.
As a result, the built-in Markdown → HWPX → rhwp PDF route prints LaTeX commands
such as `\frac` and `\sqrt` instead of typesetting fractions and roots.

This draft adds a direct Markdown → PDF edge to the Markdown converter on Windows
when Edge or Chrome is found in a standard installation directory. Markdig writes
the page, an embedded MathJax 3.2.2 bundle typesets the equations as SVG, and a
headless browser prints the result after typesetting, fonts and images are ready.
No MathJax assets are downloaded during conversion. Each conversion has its own
temporary browser profile and uses the existing `ProcessRunner` for timeout,
cancellation and process cleanup. At most two Markdown conversions run together.

The page uses Filee's existing local-picture policy and HTML sanitizer before the
trusted renderer scripts are inserted. A content security policy blocks network
resources and allows only embedded pictures/fonts and inline renderer scripts.
Code blocks are excluded from math processing. Undefined TeX commands fail the
conversion instead of silently producing broken output. Plain multiline display
math using `\\` is wrapped in `gathered`; explicit environments are kept.

MathJax's additional SVG glyph outline is disabled so regular math does not appear
emboldened. This does not remove mathematical rules or explicit bold glyph shapes.

HTML and TXT outputs are unchanged. Without a discovered browser, the old PDF
routes remain available. The direct edge is restricted to Markdown → PDF requests,
so it does not become an intermediate step for other conversions. Math is emitted
as vector outlines; the PDF's math is not searchable text.

## Design question before merging

The current browser discovery is **not aligned** with
[the external-engine policy](ADDING-A-CONVERTER.md): it finds a system-installed
browser instead of an engine managed by Filee. The direct route also takes priority
over WeasyPrint for Markdown → PDF on those hosts. This implementation is submitted
as a draft to discuss managing a Chromium engine through Filee, explicit selection,
route priority, and non-Windows support before considering it ready to merge.

## Tests

`MarkdownPdfTests` generates synthetic equations and documents. It covers route
restriction and fallback, embedded renderer resources, local pictures and script
removal, visible equation output, invalid math and cancellation. Browser-dependent
tests skip if Windows Edge/Chrome is unavailable. Existing engine tests use a
browser-independent fixture so their routes do not depend on host applications.
