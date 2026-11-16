//! Job queue: plans each request, runs files concurrently under a semaphore
//! sized to the CPU count, and streams [`JobEvent`]s over an mpsc channel.
//!
//! [`JobQueue::submit`] spawns onto the current tokio runtime, so call it from
//! inside one (Tauri commands already are).

mod job;

pub use job::{place, run_route, unique_path, Cancelled, ConvertRequest, JobEvent};

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use anyhow::{anyhow, bail, Context, Result};
use tokio::sync::{mpsc, Semaphore};
use tokio::task::JoinSet;
use tokio_util::sync::CancellationToken;

use crate::engines::Tools;
use crate::formats::{self, FormatSlice};
use crate::router::{Planner, Route};

#[derive(Clone)]
pub struct JobQueue {
    inner: Arc<Inner>,
}

struct Inner {
    planner: Planner,
    tools: Tools,
    permits: Arc<Semaphore>,
    jobs: Mutex<HashMap<String, CancellationToken>>,
    events: mpsc::UnboundedSender<JobEvent>,
    next_id: AtomicU64,
}

impl JobQueue {
    /// One concurrent conversion per CPU core.
    pub fn new(tools: Tools) -> (Self, mpsc::UnboundedReceiver<JobEvent>) {
        let cores = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(2);
        Self::with_concurrency(tools, cores)
    }

    pub fn with_concurrency(tools: Tools, concurrency: usize) -> (Self, mpsc::UnboundedReceiver<JobEvent>) {
        let (tx, rx) = mpsc::unbounded_channel();
        let inner = Inner {
            planner: Planner::new(&tools),
            tools,
            permits: Arc::new(Semaphore::new(concurrency.max(1))),
            jobs: Mutex::new(HashMap::new()),
            events: tx,
            next_id: AtomicU64::new(1),
        };
        (Self { inner: Arc::new(inner) }, rx)
    }

    pub fn planner(&self) -> &Planner {
        &self.inner.planner
    }

    pub fn tools(&self) -> &Tools {
        &self.inner.tools
    }

    /// `get_available_formats`.
    pub fn available_formats<S: AsRef<str>>(&self, extensions: &[S]) -> Vec<FormatSlice> {
        self.inner.planner.available_targets(extensions)
    }

    /// `convert_files`: validates every file has a route, then starts the job and returns its id.
    pub fn submit(&self, req: ConvertRequest) -> Result<String> {
        if req.files.is_empty() {
            bail!("no files to convert");
        }
        let target = formats::normalize(&req.target);
        let mut plans = Vec::with_capacity(req.files.len());
        for file in &req.files {
            let from = formats::of_path(file).with_context(|| format!("{} has no extension", file.display()))?;
            let route = self
                .inner
                .planner
                .plan(&from, &target)
                .ok_or_else(|| anyhow!("no conversion route from {from} to {target} ({})", file.display()))?;
            plans.push((file.clone(), route));
        }

        let id = format!("job-{}", self.inner.next_id.fetch_add(1, Ordering::Relaxed));
        let token = CancellationToken::new();
        self.inner.jobs.lock().unwrap().insert(id.clone(), token.clone());
        let inner = self.inner.clone();
        let job_id = id.clone();
        tokio::spawn(async move { inner.run_job(job_id, plans, req.output_dir, token).await });
        Ok(id)
    }

    /// `cancel_conversion`.
    pub fn cancel(&self, job_id: &str) -> Result<()> {
        let jobs = self.inner.jobs.lock().unwrap();
        let token = jobs.get(job_id).with_context(|| format!("no running job {job_id}"))?;
        token.cancel();
        Ok(())
    }
}

impl Inner {
    async fn run_job(
        self: Arc<Self>,
        id: String,
        plans: Vec<(PathBuf, Route)>,
        output_dir: Option<PathBuf>,
        token: CancellationToken,
    ) {
        let total: usize = plans.iter().map(|(_, r)| r.steps.len()).sum();
        let finished = Arc::new(AtomicUsize::new(0));
        let work_root = std::env::temp_dir().join("filee-lite").join(&id);
        let _ = self.events.send(JobEvent::Progress { job_id: id.clone(), progress: 0.0 });

        let mut set = JoinSet::new();
        for (index, (file, route)) in plans.into_iter().enumerate() {
            let inner = self.clone();
            let token = token.clone();
            let id = id.clone();
            let finished = finished.clone();
            let work = work_root.join(index.to_string());
            let dest = output_dir
                .clone()
                .or_else(|| file.parent().map(Path::to_path_buf))
                .unwrap_or_else(|| PathBuf::from("."));
            set.spawn(async move {
                let _permit = tokio::select! {
                    _ = token.cancelled() => return Err(Cancelled.into()),
                    p = inner.permits.clone().acquire_owned() => p.expect("semaphore is never closed"),
                };
                let on_step = || {
                    let n = finished.fetch_add(1, Ordering::Relaxed) + 1;
                    let _ = inner.events.send(JobEvent::Progress { job_id: id.clone(), progress: n as f32 / total as f32 });
                };
                let outputs = run_route(&inner.tools, &route, &file, &work, &token, on_step)
                    .await
                    .with_context(|| file.display().to_string())?;
                place(outputs, &dest)
            });
        }

        let mut outputs = Vec::new();
        let mut errors = Vec::new();
        while let Some(res) = set.join_next().await {
            match res {
                Ok(Ok(paths)) => outputs.extend(paths.into_iter().map(|p| p.display().to_string())),
                Ok(Err(e)) => errors.push(format!("{e:#}")),
                Err(e) => errors.push(format!("worker crashed: {e}")),
            }
        }
        let _ = tokio::fs::remove_dir_all(&work_root).await;
        self.jobs.lock().unwrap().remove(&id);

        let event = if token.is_cancelled() {
            JobEvent::Cancelled { job_id: id }
        } else if errors.is_empty() {
            JobEvent::Done { job_id: id, output_paths: outputs }
        } else {
            JobEvent::Failed { job_id: id, error: errors.join("\n"), output_paths: outputs }
        };
        let _ = self.events.send(event);
    }
}
