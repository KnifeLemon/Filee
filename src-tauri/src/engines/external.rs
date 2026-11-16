//! External tool discovery and the LibreOffice / pandoc wrappers.

use std::path::{Path, PathBuf};
use std::process::Stdio;

use anyhow::{bail, Context, Result};
use serde::Serialize;
use tokio::process::Command;

use super::{output_path, Engine};

/// External tools and optional libraries found on this machine.
/// Engines whose tool is missing are left out of the route graph.
#[derive(Clone, Debug, Default, Serialize)]
pub struct Tools {
    pub soffice: Option<PathBuf>,
    pub pandoc: Option<PathBuf>,
    pub ffmpeg: Option<PathBuf>,
    pub pdfium: bool,
}

impl Tools {
    /// Looks in `FILEE_SOFFICE` / `FILEE_PANDOC` / `FILEE_FFMPEG`, then `PATH`,
    /// then the usual install locations.
    pub fn detect() -> Self {
        Self {
            soffice: find("FILEE_SOFFICE", &["soffice", "libreoffice"], &soffice_locations()),
            pandoc: find("FILEE_PANDOC", &["pandoc"], &[]),
            ffmpeg: find("FILEE_FFMPEG", &["ffmpeg"], &[]),
            pdfium: super::pdf::available(),
        }
    }

    pub fn has(&self, engine: Engine) -> bool {
        match engine {
            Engine::LibreOffice => self.soffice.is_some(),
            Engine::Pandoc => self.pandoc.is_some(),
            Engine::Ffmpeg => self.ffmpeg.is_some(),
            Engine::Pdfium => self.pdfium,
            _ => true,
        }
    }
}

fn find(env: &str, names: &[&str], known: &[PathBuf]) -> Option<PathBuf> {
    if let Some(p) = std::env::var_os(env).map(PathBuf::from) {
        if p.is_file() {
            return Some(p);
        }
    }
    let exts: &[&str] = if cfg!(windows) { &[".exe", ".cmd", ".bat"] } else { &[""] };
    if let Some(path) = std::env::var_os("PATH") {
        for dir in std::env::split_paths(&path) {
            for name in names {
                for ext in exts {
                    let candidate = dir.join(format!("{name}{ext}"));
                    if candidate.is_file() {
                        return Some(candidate);
                    }
                }
            }
        }
    }
    known.iter().find(|p| p.is_file()).cloned()
}

fn soffice_locations() -> Vec<PathBuf> {
    let mut v = Vec::new();
    if cfg!(windows) {
        for var in ["ProgramFiles", "ProgramFiles(x86)"] {
            if let Some(base) = std::env::var_os(var) {
                v.push(PathBuf::from(base).join(r"LibreOffice\program\soffice.exe"));
            }
        }
    } else if cfg!(target_os = "macos") {
        v.push("/Applications/LibreOffice.app/Contents/MacOS/soffice".into());
    } else {
        v.push("/usr/lib/libreoffice/program/soffice".into());
        v.push("/opt/libreoffice/program/soffice".into());
    }
    v
}

/// Runs a child process to completion. The child is killed if the future is dropped.
pub(crate) async fn run(cmd: &mut Command, what: &str) -> Result<()> {
    cmd.kill_on_drop(true).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped());
    #[cfg(windows)]
    cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    let out = cmd.output().await.with_context(|| format!("cannot start {what}"))?;
    if !out.status.success() {
        let stderr = String::from_utf8_lossy(&out.stderr);
        bail!("{what} failed ({}): {}", out.status, stderr.trim());
    }
    Ok(())
}

/// `soffice --headless --convert-to <to>`: DOC/DOCX/XLSX/PPTX/HTML -> PDF, DOC -> DOCX, PPT -> PPTX.
pub async fn libreoffice(tools: &Tools, input: &Path, from: &str, to: &str, out_dir: &Path) -> Result<Vec<PathBuf>> {
    let exe = tools.soffice.as_ref().context("LibreOffice (soffice) not found")?;
    let filter = match (from, to) {
        ("html", "pdf") => "pdf:writer_web_pdf_Export",
        _ => to,
    };
    // A private profile lets conversions run while the user has LibreOffice open
    // and keeps parallel conversions from fighting over one profile lock.
    let profile = out_dir.join(".lo-profile");
    run(
        Command::new(exe)
            .arg(format!("-env:UserInstallation={}", file_url(&profile)))
            .args(["--headless", "--norestore", "--nolockcheck", "--convert-to", filter, "--outdir"])
            .arg(out_dir)
            .arg(input),
        "LibreOffice",
    )
    .await?;
    let _ = std::fs::remove_dir_all(&profile);
    let out = output_path(input, to, out_dir);
    if !out.is_file() {
        bail!("LibreOffice produced no {to} for {}", input.display());
    }
    Ok(vec![out])
}

/// `pandoc`: Markdown -> DOCX, DOCX -> HTML.
pub async fn pandoc(tools: &Tools, input: &Path, from: &str, to: &str, out_dir: &Path) -> Result<Vec<PathBuf>> {
    let exe = tools.pandoc.as_ref().context("pandoc not found")?;
    let name = |f: &str| if f == "md" { "gfm".to_string() } else { f.to_string() };
    let out = output_path(input, to, out_dir);
    let mut cmd = Command::new(exe);
    cmd.arg(input).args(["-f", &name(from), "-t", &name(to), "-o"]).arg(&out);
    if to == "html" {
        cmd.args(["--standalone", "--metadata"]).arg(format!("title={}", super::stem(input)));
    }
    run(&mut cmd, "pandoc").await?;
    Ok(vec![out])
}

fn file_url(path: &Path) -> String {
    let s = path.to_string_lossy().replace('\\', "/").replace(' ', "%20");
    if s.starts_with('/') { format!("file://{s}") } else { format!("file:///{s}") }
}
