//! HWP / HWPX via the pure-Rust `unhwp` crate.

use std::path::Path;

use anyhow::{anyhow, bail, Context, Result};

pub fn convert(input: &Path, to: &str, out: &Path) -> Result<()> {
    let content = match to {
        "md" => unhwp::to_markdown(input).map_err(err)?,
        "txt" => unhwp::extract_text(input).map_err(err)?,
        "json" => unhwp::parse_file(input).map_err(err)?.raw_content().to_string(),
        other => bail!("unhwp cannot write {other}"),
    };
    std::fs::write(out, content).with_context(|| format!("cannot write {}", out.display()))
}

/// HWP -> Markdown -> HTML in one step, so HWP -> PDF -> PNG stays within the 3-hop limit.
pub fn to_html(input: &Path, out: &Path) -> Result<()> {
    let md = unhwp::to_markdown(input).map_err(err)?;
    let html = super::markdown::render_html(&md, &super::stem(input));
    std::fs::write(out, html).with_context(|| format!("cannot write {}", out.display()))
}

fn err(e: impl std::fmt::Display) -> anyhow::Error {
    anyhow!("unhwp: {e}")
}
