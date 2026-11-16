//! CLI prototype (Phase 1+2): `filee-cli convert report.hwp --to md`.

use std::io::Write;
use std::path::PathBuf;
use std::process::ExitCode;

use anyhow::{bail, Result};
use clap::{Parser, Subcommand};
use filee_lite::formats;
use filee_lite::{ConvertRequest, JobEvent, JobQueue, Planner, Tools};

#[derive(Parser)]
#[command(name = "filee-cli", version, about = "Drag. Drop. Converted. — CLI prototype")]
struct Cli {
    #[command(subcommand)]
    cmd: Cmd,
}

#[derive(Subcommand)]
enum Cmd {
    /// Show which external tools were found
    Tools,
    /// List the target formats available for these files (or bare extensions)
    Formats {
        #[arg(required = true)]
        files: Vec<String>,
    },
    /// Show the conversion route between two formats
    Route { from: String, to: String },
    /// Convert files
    Convert {
        #[arg(required = true)]
        files: Vec<PathBuf>,
        /// Target format, e.g. pdf, md, png
        #[arg(short, long)]
        to: String,
        /// Output folder (default: next to each input)
        #[arg(short, long)]
        out: Option<PathBuf>,
        /// Max concurrent conversions (default: CPU cores)
        #[arg(short, long)]
        jobs: Option<usize>,
    },
}

#[tokio::main]
async fn main() -> ExitCode {
    match run(Cli::parse()).await {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("error: {e:#}");
            ExitCode::FAILURE
        }
    }
}

async fn run(cli: Cli) -> Result<()> {
    let tools = Tools::detect();
    match cli.cmd {
        Cmd::Tools => {
            let show = |name: &str, p: &Option<PathBuf>| match p {
                Some(p) => println!("{name:<12} {}", p.display()),
                None => println!("{name:<12} (not found)"),
            };
            show("libreoffice", &tools.soffice);
            show("pandoc", &tools.pandoc);
            show("ffmpeg", &tools.ffmpeg);
            println!("{:<12} {}", "pdfium", if tools.pdfium { "loaded" } else { "(not found)" });
        }
        Cmd::Formats { files } => {
            let exts: Vec<String> = files
                .iter()
                .map(|f| formats::of_path(f.as_ref()).unwrap_or_else(|| formats::normalize(f)))
                .collect();
            for slice in Planner::new(&tools).available_targets(&exts) {
                println!("{:<6} {}", slice.id, slice.category);
            }
        }
        Cmd::Route { from, to } => match Planner::new(&tools).plan(&from, &to) {
            Some(route) => println!("{route}"),
            None => bail!("no route from {from} to {to} with the tools on this machine"),
        },
        Cmd::Convert { files, to, out, jobs } => {
            let (queue, mut events) = match jobs {
                Some(n) => JobQueue::with_concurrency(tools, n),
                None => JobQueue::new(tools),
            };
            let job = queue.submit(ConvertRequest { files, target: to, output_dir: out })?;
            while let Some(event) = events.recv().await {
                if event.job_id() != job {
                    continue;
                }
                match event {
                    JobEvent::Progress { progress, .. } => {
                        eprint!("\r{:>3}%", (progress * 100.0).round() as u32);
                        let _ = std::io::stderr().flush();
                    }
                    JobEvent::Done { output_paths, .. } => {
                        eprintln!();
                        output_paths.iter().for_each(|p| println!("{p}"));
                        return Ok(());
                    }
                    JobEvent::Failed { error, output_paths, .. } => {
                        eprintln!();
                        output_paths.iter().for_each(|p| println!("{p}"));
                        bail!("{error}");
                    }
                    JobEvent::Cancelled { .. } => bail!("cancelled"),
                }
            }
            bail!("event channel closed before the job finished");
        }
    }
    Ok(())
}
