//! Format ids, aliases and categories.

use std::path::Path;

use serde::Serialize;

/// One slice of the donut: a target format the dragged files can be converted to.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct FormatSlice {
    pub id: String,
    pub label: String,
    pub category: &'static str,
}

impl FormatSlice {
    pub fn new(id: &str) -> Self {
        Self { id: id.to_string(), label: id.to_ascii_uppercase(), category: category(id) }
    }
}

/// Lowercases, strips a leading dot and folds aliases (`jpeg` -> `jpg`).
pub fn normalize(ext: &str) -> String {
    let ext = ext.trim().trim_start_matches('.').to_ascii_lowercase();
    match ext.as_str() {
        "jpeg" | "jpe" => "jpg".into(),
        "htm" => "html".into(),
        "markdown" => "md".into(),
        "text" => "txt".into(),
        _ => ext,
    }
}

/// Normalized format of a file, taken from its extension.
pub fn of_path(path: &Path) -> Option<String> {
    path.extension().and_then(|e| e.to_str()).map(normalize).filter(|e| !e.is_empty())
}

pub fn category(format: &str) -> &'static str {
    match format {
        "hwp" | "hwpx" => "hwp",
        "docx" | "doc" => "word",
        "xlsx" | "xls" | "xlsb" | "ods" | "csv" => "excel",
        "pptx" | "ppt" => "powerpoint",
        "png" | "jpg" | "webp" | "gif" | "svg" | "ico" | "bmp" => "image",
        "mp4" | "webm" | "mkv" | "mp3" | "wav" | "aac" => "media",
        _ => "document",
    }
}

/// Sort key so the donut groups slices by category.
pub fn category_rank(category: &str) -> u8 {
    match category {
        "document" => 0,
        "hwp" => 1,
        "word" => 2,
        "excel" => 3,
        "powerpoint" => 4,
        "image" => 5,
        "media" => 6,
        _ => 7,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_aliases() {
        assert_eq!(normalize(".JPEG"), "jpg");
        assert_eq!(normalize("Hwpx"), "hwpx");
        assert_eq!(of_path(Path::new("a/b/보고서.HWP")).as_deref(), Some("hwp"));
        assert_eq!(of_path(Path::new("noext")), None);
    }
}
