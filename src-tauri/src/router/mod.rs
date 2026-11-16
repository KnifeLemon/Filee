//! Conversion graph: formats are nodes, engines are weighted edges.

mod planner;

pub use planner::{Planner, Route, Step, MAX_HOPS};

use crate::engines::Engine;

/// Edge costs: pure Rust is cheapest, then process launches, LibreOffice last.
const RUST: u32 = 1;
const PDFIUM: u32 = 2;
const FFMPEG: u32 = 2;
const PANDOC: u32 = 3;
const OFFICE: u32 = 5;

const SPREADSHEETS_IN: &[&str] = &["xlsx", "xls", "xlsb", "ods", "csv"];
const SPREADSHEETS_OUT: &[&str] = &["csv", "json", "md", "xlsx"];
const RASTER_IN: &[&str] = &["png", "jpg", "webp", "gif", "bmp", "ico"];
const RASTER_OUT: &[&str] = &["png", "jpg", "webp", "ico", "bmp"];
const VIDEO_IN: &[&str] = &["mp4", "webm", "mkv"];
const VIDEO_OUT: &[&str] = &["mp4", "webm", "mp3", "wav"];
const AUDIO_IN: &[&str] = &["mp3", "wav", "aac"];
const AUDIO_OUT: &[&str] = &["mp3", "wav"];

/// Every conversion edge `(from, to, engine, cost)`. Self-loops are dropped.
pub fn edges() -> Vec<(&'static str, &'static str, Engine, u32)> {
    let mut e = Vec::new();
    let mut all = |froms: &[&'static str], tos: &[&'static str], engine: Engine, cost: u32| {
        for &f in froms {
            for &t in tos {
                if f != t {
                    e.push((f, t, engine, cost));
                }
            }
        }
    };

    // HWP / HWPX
    all(&["hwp", "hwpx"], &["md", "txt", "json"], Engine::Unhwp, RUST);
    all(&["hwp", "hwpx"], &["html"], Engine::UnhwpHtml, RUST + 1);

    // Markdown / HTML
    all(&["md"], &["html"], Engine::Markdown, RUST);
    all(&["md"], &["docx"], Engine::Pandoc, PANDOC);
    all(&["html"], &["pdf"], Engine::LibreOffice, OFFICE);

    // Word
    all(&["docx"], &["txt", "md"], Engine::WordText, RUST);
    all(&["docx"], &["html"], Engine::Pandoc, PANDOC);
    all(&["docx", "doc"], &["pdf"], Engine::LibreOffice, OFFICE);
    all(&["doc"], &["docx"], Engine::LibreOffice, OFFICE);

    // Excel
    all(SPREADSHEETS_IN, SPREADSHEETS_OUT, Engine::Spreadsheet, RUST);
    all(SPREADSHEETS_IN, &["pdf"], Engine::LibreOffice, OFFICE);

    // PowerPoint
    all(&["pptx"], &["txt"], Engine::PptText, RUST);
    all(&["pptx", "ppt"], &["pdf"], Engine::LibreOffice, OFFICE);
    all(&["ppt"], &["pptx"], Engine::LibreOffice, OFFICE);

    // PDF pages -> images
    all(&["pdf"], &["png"], Engine::Pdfium, PDFIUM);

    // Images
    all(RASTER_IN, RASTER_OUT, Engine::Raster, RUST);
    all(&["svg"], &["png"], Engine::Svg, RUST);

    // Media
    all(VIDEO_IN, VIDEO_OUT, Engine::Ffmpeg, FFMPEG);
    all(AUDIO_IN, AUDIO_OUT, Engine::Ffmpeg, FFMPEG);

    e
}
