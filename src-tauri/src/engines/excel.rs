//! Spreadsheets: read with `calamine` (XLSX/XLS/XLSB/ODS) or `csv`,
//! write CSV / JSON / Markdown / XLSX (`rust_xlsxwriter`).

use std::fs::File;
use std::io::Write;
use std::path::{Path, PathBuf};

use anyhow::{bail, Context, Result};
use calamine::{open_workbook_auto, Data, Reader};
use rust_xlsxwriter::Workbook;
use serde_json::{json, Value};

use super::{output_path, stem};

#[derive(Clone, Debug, PartialEq)]
pub enum Cell {
    Empty,
    Int(i64),
    Float(f64),
    Bool(bool),
    Text(String),
}

impl Cell {
    fn text(&self) -> String {
        match self {
            Cell::Empty => String::new(),
            Cell::Int(i) => i.to_string(),
            Cell::Float(f) => f.to_string(),
            Cell::Bool(b) => if *b { "TRUE".into() } else { "FALSE".into() },
            Cell::Text(s) => s.clone(),
        }
    }

    fn json(&self) -> Value {
        match self {
            Cell::Empty => Value::Null,
            Cell::Int(i) => json!(i),
            Cell::Float(f) => json!(f),
            Cell::Bool(b) => json!(b),
            Cell::Text(s) => json!(s),
        }
    }

    /// CSV values are untyped; keep identifiers like "007" as text.
    fn guess(s: &str) -> Cell {
        let t = s.trim();
        if t.is_empty() {
            return Cell::Empty;
        }
        let leading_zero = t.len() > 1 && t.starts_with('0') && !t.starts_with("0.");
        if !leading_zero {
            if let Ok(i) = t.parse::<i64>() {
                return Cell::Int(i);
            }
            if let Ok(f) = t.parse::<f64>() {
                if f.is_finite() {
                    return Cell::Float(f);
                }
            }
        }
        Cell::Text(s.to_string())
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct Sheet {
    pub name: String,
    pub rows: Vec<Vec<Cell>>,
}

pub fn convert(input: &Path, from: &str, to: &str, out_dir: &Path) -> Result<Vec<PathBuf>> {
    let sheets = if from == "csv" { read_csv(input)? } else { read_workbook(input)? };
    match to {
        "csv" => write_csv_files(&sheets, input, out_dir),
        "json" => single(input, to, out_dir, |p| write_json(&sheets, p)),
        "md" => single(input, to, out_dir, |p| write_markdown(&sheets, p)),
        "xlsx" => single(input, to, out_dir, |p| write_xlsx(&sheets, p)),
        other => bail!("spreadsheet engine cannot write {other}"),
    }
}

fn single(input: &Path, to: &str, out_dir: &Path, write: impl FnOnce(&Path) -> Result<()>) -> Result<Vec<PathBuf>> {
    let out = output_path(input, to, out_dir);
    write(&out)?;
    Ok(vec![out])
}

pub fn read_workbook(input: &Path) -> Result<Vec<Sheet>> {
    let mut wb = open_workbook_auto(input).with_context(|| format!("cannot open {}", input.display()))?;
    let names = wb.sheet_names().to_owned();
    let mut sheets = Vec::with_capacity(names.len());
    for name in names {
        let range = wb.worksheet_range(&name).with_context(|| format!("cannot read sheet {name}"))?;
        // calamine trims leading empty rows/columns; put them back so A1 stays A1.
        let (top, left) = range.start().map(|(r, c)| (r as usize, c as usize)).unwrap_or((0, 0));
        let mut rows: Vec<Vec<Cell>> = vec![Vec::new(); top];
        for row in range.rows() {
            let mut cells = vec![Cell::Empty; left];
            cells.extend(row.iter().map(cell));
            rows.push(cells);
        }
        sheets.push(Sheet { name, rows });
    }
    Ok(sheets)
}

fn cell(d: &Data) -> Cell {
    match d {
        Data::Empty => Cell::Empty,
        Data::Int(i) => Cell::Int(*i),
        Data::Float(f) => Cell::Float(*f),
        Data::Bool(b) => Cell::Bool(*b),
        Data::String(s) => Cell::Text(s.clone()),
        other => Cell::Text(other.to_string()),
    }
}

pub fn read_csv(input: &Path) -> Result<Vec<Sheet>> {
    let mut reader = csv::ReaderBuilder::new()
        .has_headers(false)
        .flexible(true)
        .from_path(input)
        .with_context(|| format!("cannot open {}", input.display()))?;
    let mut rows = Vec::new();
    for record in reader.byte_records() {
        let record = record?;
        rows.push(record.iter().map(|f| Cell::guess(&String::from_utf8_lossy(f))).collect());
    }
    Ok(vec![Sheet { name: stem(input), rows }])
}

fn write_csv_files(sheets: &[Sheet], input: &Path, out_dir: &Path) -> Result<Vec<PathBuf>> {
    let mut outputs = Vec::new();
    for sheet in sheets {
        let out = if sheets.len() == 1 {
            output_path(input, "csv", out_dir)
        } else {
            out_dir.join(format!("{}-{}.csv", stem(input), safe_file_name(&sheet.name)))
        };
        let mut file = File::create(&out).with_context(|| format!("cannot write {}", out.display()))?;
        file.write_all(b"\xEF\xBB\xBF")?; // BOM so Excel reads Korean text as UTF-8
        let mut w = csv::WriterBuilder::new().flexible(true).from_writer(file);
        for row in &sheet.rows {
            w.write_record(row.iter().map(Cell::text))?;
        }
        w.flush()?;
        outputs.push(out);
    }
    Ok(outputs)
}

fn write_json(sheets: &[Sheet], out: &Path) -> Result<()> {
    let doc: Vec<Value> = sheets
        .iter()
        .map(|s| {
            let rows: Vec<Value> = s.rows.iter().map(|r| Value::Array(r.iter().map(Cell::json).collect())).collect();
            json!({ "name": s.name, "rows": rows })
        })
        .collect();
    let file = File::create(out).with_context(|| format!("cannot write {}", out.display()))?;
    serde_json::to_writer_pretty(file, &json!({ "sheets": doc }))?;
    Ok(())
}

pub fn markdown(sheets: &[Sheet]) -> String {
    let mut md = String::new();
    for sheet in sheets.iter().filter(|s| !s.rows.is_empty()) {
        if sheets.len() > 1 {
            md.push_str(&format!("## {}\n\n", sheet.name));
        }
        let width = sheet.rows.iter().map(Vec::len).max().unwrap_or(0).max(1);
        let line = |row: &[Cell]| {
            let cells: Vec<String> = (0..width)
                .map(|i| row.get(i).map(Cell::text).unwrap_or_default().replace('|', "\\|").replace(['\r', '\n'], " "))
                .collect();
            format!("| {} |\n", cells.join(" | "))
        };
        md.push_str(&line(&sheet.rows[0]));
        md.push_str(&format!("|{}\n", " --- |".repeat(width)));
        for row in &sheet.rows[1..] {
            md.push_str(&line(row));
        }
        md.push('\n');
    }
    md
}

fn write_markdown(sheets: &[Sheet], out: &Path) -> Result<()> {
    std::fs::write(out, markdown(sheets)).with_context(|| format!("cannot write {}", out.display()))
}

fn write_xlsx(sheets: &[Sheet], out: &Path) -> Result<()> {
    const MAX_ROWS: usize = 1_048_576;
    const MAX_COLS: usize = 16_384;
    let mut wb = Workbook::new();
    let mut used: Vec<String> = Vec::new();
    for sheet in sheets {
        let ws = wb.add_worksheet();
        ws.set_name(unique_sheet_name(&sheet.name, &mut used))?;
        if sheet.rows.len() > MAX_ROWS {
            bail!("sheet {} has more than {MAX_ROWS} rows", sheet.name);
        }
        for (r, row) in sheet.rows.iter().enumerate() {
            if row.len() > MAX_COLS {
                bail!("sheet {} has more than {MAX_COLS} columns", sheet.name);
            }
            for (c, value) in row.iter().enumerate() {
                let (r, c) = (r as u32, c as u16);
                match value {
                    Cell::Empty => {}
                    Cell::Int(i) => { ws.write_number(r, c, *i as f64)?; }
                    Cell::Float(f) => { ws.write_number(r, c, *f)?; }
                    Cell::Bool(b) => { ws.write_boolean(r, c, *b)?; }
                    Cell::Text(s) => { ws.write_string(r, c, s)?; }
                }
            }
        }
    }
    if sheets.is_empty() {
        wb.add_worksheet();
    }
    wb.save(out).with_context(|| format!("cannot write {}", out.display()))?;
    Ok(())
}

/// Excel sheet names: max 31 chars, none of `[]:*?/\`, unique (case-insensitive).
fn unique_sheet_name(name: &str, used: &mut Vec<String>) -> String {
    let clean: String = name.chars().filter(|c| !"[]:*?/\\".contains(*c)).take(31).collect();
    let base = if clean.trim().is_empty() { "Sheet".to_string() } else { clean };
    let mut candidate = base.clone();
    let mut n = 2;
    while used.iter().any(|u| u.eq_ignore_ascii_case(&candidate)) {
        let suffix = format!(" ({n})");
        let keep: String = base.chars().take(31 - suffix.chars().count()).collect();
        candidate = format!("{keep}{suffix}");
        n += 1;
    }
    used.push(candidate.clone());
    candidate
}

fn safe_file_name(name: &str) -> String {
    name.chars().map(|c| if "<>:\"/\\|?*".contains(c) || c.is_control() { '_' } else { c }).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guesses_csv_types() {
        assert_eq!(Cell::guess("42"), Cell::Int(42));
        assert_eq!(Cell::guess("3.5"), Cell::Float(3.5));
        assert_eq!(Cell::guess("007"), Cell::Text("007".into()));
        assert_eq!(Cell::guess("0.5"), Cell::Float(0.5));
        assert_eq!(Cell::guess(""), Cell::Empty);
        assert_eq!(Cell::guess("NaN"), Cell::Text("NaN".into()));
    }

    #[test]
    fn markdown_table() {
        let sheets = vec![Sheet {
            name: "s".into(),
            rows: vec![vec![Cell::Text("이름".into()), Cell::Text("a|b".into())], vec![Cell::Int(1)]],
        }];
        assert_eq!(markdown(&sheets), "| 이름 | a\\|b |\n| --- | --- |\n| 1 |  |\n\n");
    }

    #[test]
    fn sheet_names_are_valid_and_unique() {
        let mut used = Vec::new();
        assert_eq!(unique_sheet_name("a/b", &mut used), "ab");
        assert_eq!(unique_sheet_name("AB", &mut used), "AB (2)");
        assert_eq!(unique_sheet_name("", &mut used), "Sheet");
        assert_eq!(unique_sheet_name(&"x".repeat(40), &mut used).chars().count(), 31);
    }
}
