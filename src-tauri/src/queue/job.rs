//! IPC types and the per-file route executor.

use std::path::{Path, PathBuf};

use anyhow::{bail, Context, Result};
use serde::{Deserialize, Serialize};
use tokio_util::sync::CancellationToken;

use crate::engines::{self, Tools};
use crate::router::Route;

/// `convert_files` payload.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ConvertRequest {
    pub files: Vec<PathBuf>,
    /// Target format id, e.g. `"pdf"`.
    pub target: String,
    /// Where results go; defaults to each input's own folder.
    #[serde(default)]
    pub output_dir: Option<PathBuf>,
}

/// Events streamed to the frontend.
#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum JobEvent {
    Progress { job_id: String, progress: f32 },
    Done { job_id: String, output_paths: Vec<String> },
    /// Some or all files failed; `output_paths` lists the ones that succeeded.
    Failed { job_id: String, error: String, output_paths: Vec<String> },
    Cancelled { job_id: String },
}

impl JobEvent {
    /// Tauri event name (`conversion:progress`, ...).
    pub fn name(&self) -> &'static str {
        match self {
            JobEvent::Progress { .. } => "conversion:progress",
            JobEvent::Done { .. } => "conversion:done",
            JobEvent::Failed { .. } => "conversion:failed",
            JobEvent::Cancelled { .. } => "conversion:cancelled",
        }
    }

    pub fn job_id(&self) -> &str {
        match self {
            JobEvent::Progress { job_id, .. }
            | JobEvent::Done { job_id, .. }
            | JobEvent::Failed { job_id, .. }
            | JobEvent::Cancelled { job_id } => job_id,
        }
    }
}

#[derive(Debug)]
pub struct Cancelled;

impl std::fmt::Display for Cancelled {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("cancelled")
    }
}

impl std::error::Error for Cancelled {}

/// Runs every step of `route` in `work`. A step that yields several files
/// (one PNG per slide) fans out: the next step runs once per file.
pub async fn run_route(
    tools: &Tools,
    route: &Route,
    input: &Path,
    work: &Path,
    cancel: &CancellationToken,
    on_step: impl Fn(),
) -> Result<Vec<PathBuf>> {
    let mut current = vec![input.to_path_buf()];
    for (i, step) in route.steps.iter().enumerate() {
        let dir = work.join(format!("step{i}"));
        let mut next = Vec::new();
        for file in &current {
            let outputs = tokio::select! {
                _ = cancel.cancelled() => return Err(Cancelled.into()),
                r = engines::run(step.engine, tools, file, &step.from, &step.to, &dir) => r?,
            };
            next.extend(outputs);
        }
        if next.is_empty() {
            bail!("{} produced no output", step.engine.name());
        }
        current = next;
        on_step();
    }
    Ok(current)
}

/// Moves results into `dest`, never overwriting: `report.pdf`, `report (1).pdf`, ...
pub fn place(outputs: Vec<PathBuf>, dest: &Path) -> Result<Vec<PathBuf>> {
    std::fs::create_dir_all(dest).with_context(|| format!("cannot create {}", dest.display()))?;
    let mut placed = Vec::with_capacity(outputs.len());
    for src in outputs {
        let name = src.file_name().context("output without file name")?;
        let target = unique_path(&dest.join(name));
        if std::fs::rename(&src, &target).is_err() {
            // Different volume: copy instead.
            std::fs::copy(&src, &target).with_context(|| format!("cannot write {}", target.display()))?;
            let _ = std::fs::remove_file(&src);
        }
        placed.push(target);
    }
    Ok(placed)
}

pub fn unique_path(path: &Path) -> PathBuf {
    if !path.exists() {
        return path.to_path_buf();
    }
    let stem = path.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
    let ext = path.extension().map(|e| format!(".{}", e.to_string_lossy())).unwrap_or_default();
    (1..)
        .map(|n| path.with_file_name(format!("{stem} ({n}){ext}")))
        .find(|p| !p.exists())
        .expect("unbounded range")
}
