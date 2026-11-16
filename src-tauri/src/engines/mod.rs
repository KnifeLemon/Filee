//! Conversion engines.
//!
//! Every engine converts one input file into one or more files inside `out_dir`,
//! named after the input's stem (`report.pdf`, or `deck-001.png`, `deck-002.png`, ...).
//! Pure-Rust engines run on the blocking pool; external tools run as child processes
//! that are killed when the future is dropped (that is how cancellation works).

use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use serde::Serialize;

pub mod excel;
pub mod external;
pub mod hwp;
pub mod markdown;
pub mod media;
pub mod ooxml;
pub mod pdf;
pub mod ppt;
pub mod raster;
pub mod word;

pub use external::Tools;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Engine {
    /// `unhwp`: HWP/HWPX -> Markdown / text / JSON.
    Unhwp,
    /// `unhwp` + `pulldown-cmark`: HWP/HWPX -> HTML in one step.
    UnhwpHtml,
    /// `pulldown-cmark`: Markdown -> HTML.
    Markdown,
    /// `zip` + `quick-xml`: DOCX -> text / Markdown.
    WordText,
    /// `zip` + `quick-xml`: PPTX -> text.
    PptText,
    /// `calamine` + `rust_xlsxwriter` + `csv`: spreadsheet <-> CSV / JSON / Markdown / XLSX.
    Spreadsheet,
    /// `image`: raster <-> raster.
    Raster,
    /// `resvg`: SVG -> PNG.
    Svg,
    /// `pdfium-render`: PDF -> PNG per page.
    Pdfium,
    /// `soffice --headless --convert-to`.
    LibreOffice,
    /// `pandoc`.
    Pandoc,
    /// `ffmpeg`.
    Ffmpeg,
}

impl Engine {
    pub fn name(self) -> &'static str {
        match self {
            Engine::Unhwp => "unhwp",
            Engine::UnhwpHtml => "unhwp+pulldown-cmark",
            Engine::Markdown => "pulldown-cmark",
            Engine::WordText => "docx-xml",
            Engine::PptText => "pptx-xml",
            Engine::Spreadsheet => "calamine/rust_xlsxwriter",
            Engine::Raster => "image",
            Engine::Svg => "resvg",
            Engine::Pdfium => "pdfium",
            Engine::LibreOffice => "libreoffice",
            Engine::Pandoc => "pandoc",
            Engine::Ffmpeg => "ffmpeg",
        }
    }
}

/// Runs one conversion step.
pub async fn run(
    engine: Engine,
    tools: &Tools,
    input: &Path,
    from: &str,
    to: &str,
    out_dir: &Path,
) -> Result<Vec<PathBuf>> {
    std::fs::create_dir_all(out_dir)
        .with_context(|| format!("cannot create {}", out_dir.display()))?;
    match engine {
        Engine::LibreOffice => external::libreoffice(tools, input, from, to, out_dir).await,
        Engine::Pandoc => external::pandoc(tools, input, from, to, out_dir).await,
        Engine::Ffmpeg => media::convert(tools, input, to, out_dir).await,
        _ => {
            let (input, from, to, out_dir) =
                (input.to_path_buf(), from.to_string(), to.to_string(), out_dir.to_path_buf());
            tokio::task::spawn_blocking(move || run_blocking(engine, &input, &from, &to, &out_dir))
                .await
                .context("engine thread panicked")?
        }
    }
}

fn run_blocking(engine: Engine, input: &Path, from: &str, to: &str, out_dir: &Path) -> Result<Vec<PathBuf>> {
    let out = output_path(input, to, out_dir);
    match engine {
        Engine::Unhwp => hwp::convert(input, to, &out)?,
        Engine::UnhwpHtml => hwp::to_html(input, &out)?,
        Engine::Markdown => markdown::convert(input, to, &out)?,
        Engine::WordText => word::convert(input, to, &out)?,
        Engine::PptText => ppt::convert(input, to, &out)?,
        Engine::Raster => raster::convert(input, to, &out)?,
        Engine::Svg => raster::svg_to_png(input, &out)?,
        Engine::Spreadsheet => return excel::convert(input, from, to, out_dir),
        Engine::Pdfium => return pdf::to_png(input, out_dir),
        Engine::LibreOffice | Engine::Pandoc | Engine::Ffmpeg => unreachable!("async engine"),
    }
    Ok(vec![out])
}

pub fn stem(input: &Path) -> String {
    input.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_else(|| "output".into())
}

pub fn output_path(input: &Path, ext: &str, out_dir: &Path) -> PathBuf {
    out_dir.join(format!("{}.{ext}", stem(input)))
}

/// Path for the n-th (1-based) of several outputs: `stem-001.ext`.
pub fn numbered_path(input: &Path, n: usize, ext: &str, out_dir: &Path) -> PathBuf {
    out_dir.join(format!("{}-{n:03}.{ext}", stem(input)))
}
