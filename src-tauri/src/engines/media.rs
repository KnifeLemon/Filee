//! Audio / video via the `ffmpeg` binary.

use std::path::{Path, PathBuf};

use anyhow::{bail, Context, Result};
use tokio::process::Command;

use super::{external, output_path, Tools};

pub async fn convert(tools: &Tools, input: &Path, to: &str, out_dir: &Path) -> Result<Vec<PathBuf>> {
    let exe = tools.ffmpeg.as_ref().context("ffmpeg not found")?;
    let codec: &[&str] = match to {
        "mp3" => &["-vn", "-c:a", "libmp3lame", "-q:a", "2"],
        "wav" => &["-vn", "-c:a", "pcm_s16le"],
        "mp4" => &[
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart",
        ],
        "webm" => &["-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "33", "-row-mt", "1", "-c:a", "libopus"],
        other => bail!("ffmpeg engine cannot write {other}"),
    };
    let out = output_path(input, to, out_dir);
    external::run(
        Command::new(exe).args(["-y", "-hide_banner", "-loglevel", "error", "-i"]).arg(input).args(codec).arg(&out),
        "ffmpeg",
    )
    .await?;
    Ok(vec![out])
}
