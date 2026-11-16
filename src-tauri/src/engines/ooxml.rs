//! Shared helpers for OOXML (ZIP) packages.

use std::fs::File;
use std::io::Read;
use std::path::Path;

use anyhow::{Context, Result};
use quick_xml::events::BytesStart;
use zip::result::ZipError;
use zip::ZipArchive;

pub fn open(path: &Path) -> Result<ZipArchive<File>> {
    let file = File::open(path).with_context(|| format!("cannot open {}", path.display()))?;
    ZipArchive::new(file).with_context(|| format!("{} is not an OOXML (zip) package", path.display()))
}

/// Reads one entry as UTF-8; `None` when the entry does not exist.
pub fn read_entry(zip: &mut ZipArchive<File>, name: &str) -> Result<Option<String>> {
    match zip.by_name(name) {
        Ok(mut entry) => {
            let mut s = String::new();
            entry.read_to_string(&mut s).with_context(|| format!("cannot read {name}"))?;
            Ok(Some(s))
        }
        Err(ZipError::FileNotFound) => Ok(None),
        Err(e) => Err(e.into()),
    }
}

/// Value of an attribute such as `w:val`, unescaped.
pub fn attr(e: &BytesStart, name: &str) -> Result<Option<String>> {
    Ok(match e.try_get_attribute(name)? {
        Some(a) => Some(a.unescape_value()?.into_owned()),
        None => None,
    })
}
