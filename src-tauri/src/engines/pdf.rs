//! PDF -> PNG (one file per page) via `pdfium-render`.
//!
//! The pdfium shared library is looked up in `FILEE_PDFIUM_DIR`, next to the
//! executable, then on the system library path. Without it the PDF -> PNG edge
//! is simply absent from the route graph.

use std::path::{Path, PathBuf};

use anyhow::Result;

#[cfg(feature = "pdfium")]
mod imp {
    use std::path::{Path, PathBuf};
    use std::sync::OnceLock;

    use anyhow::{anyhow, Context, Result};
    use pdfium_render::prelude::*;

    use crate::engines::{numbered_path, output_path};

    // pdfium may only be bound once per process.
    static PDFIUM: OnceLock<Option<Pdfium>> = OnceLock::new();

    fn pdfium() -> Option<&'static Pdfium> {
        PDFIUM
            .get_or_init(|| {
                let mut dirs: Vec<PathBuf> = Vec::new();
                if let Some(dir) = std::env::var_os("FILEE_PDFIUM_DIR") {
                    dirs.push(dir.into());
                }
                if let Some(dir) = std::env::current_exe().ok().and_then(|e| e.parent().map(Path::to_path_buf)) {
                    dirs.push(dir);
                }
                for dir in &dirs {
                    if let Ok(b) = Pdfium::bind_to_library(Pdfium::pdfium_platform_library_name_at_path(dir)) {
                        return Some(Pdfium::new(b));
                    }
                }
                Pdfium::bind_to_system_library().ok().map(Pdfium::new)
            })
            .as_ref()
    }

    pub fn available() -> bool {
        pdfium().is_some()
    }

    pub fn to_png(input: &Path, out_dir: &Path) -> Result<Vec<PathBuf>> {
        let pdfium = pdfium().context("pdfium library not found")?;
        let doc = pdfium
            .load_pdf_from_file(input, None)
            .map_err(|e| anyhow!("pdfium cannot open {}: {e}", input.display()))?;
        // ~200 dpi for A4 portrait; tall pages are capped.
        let config = PdfRenderConfig::new().set_target_width(1654).set_maximum_height(4000);
        let pages = doc.pages();
        let count = pages.len() as usize;
        let mut outputs = Vec::with_capacity(count);
        for (i, page) in pages.iter().enumerate() {
            let out = if count == 1 { output_path(input, "png", out_dir) } else { numbered_path(input, i + 1, "png", out_dir) };
            page.render_with_config(&config)
                .map_err(|e| anyhow!("pdfium cannot render page {}: {e}", i + 1))?
                .as_image()
                .save(&out)
                .with_context(|| format!("cannot write {}", out.display()))?;
            outputs.push(out);
        }
        Ok(outputs)
    }
}

#[cfg(feature = "pdfium")]
pub fn available() -> bool {
    imp::available()
}

#[cfg(feature = "pdfium")]
pub fn to_png(input: &Path, out_dir: &Path) -> Result<Vec<PathBuf>> {
    imp::to_png(input, out_dir)
}

#[cfg(not(feature = "pdfium"))]
pub fn available() -> bool {
    false
}

#[cfg(not(feature = "pdfium"))]
pub fn to_png(_input: &Path, _out_dir: &Path) -> Result<Vec<PathBuf>> {
    anyhow::bail!("built without the `pdfium` feature")
}
