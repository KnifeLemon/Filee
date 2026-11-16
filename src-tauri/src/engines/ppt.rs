//! PPTX -> text, slide by slide, from `ppt/slides/slideN.xml`.

use std::path::Path;

use anyhow::{bail, Context, Result};
use quick_xml::events::Event;
use quick_xml::Reader;

use super::ooxml;

pub fn convert(input: &Path, to: &str, out: &Path) -> Result<()> {
    if to != "txt" {
        bail!("pptx text engine cannot write {to}");
    }
    let slides = read_slides(input)?;
    let mut text = String::new();
    for (i, slide) in slides.iter().enumerate() {
        if i > 0 {
            text.push('\n');
        }
        text.push_str(&format!("--- Slide {} ---\n{}\n", i + 1, slide.trim_end()));
    }
    std::fs::write(out, text).with_context(|| format!("cannot write {}", out.display()))
}

pub fn read_slides(input: &Path) -> Result<Vec<String>> {
    let mut zip = ooxml::open(input)?;
    let mut names: Vec<(u32, String)> = zip
        .file_names()
        .filter_map(|n| {
            let num = n.strip_prefix("ppt/slides/slide")?.strip_suffix(".xml")?.parse().ok()?;
            Some((num, n.to_string()))
        })
        .collect();
    names.sort();
    let mut slides = Vec::with_capacity(names.len());
    for (_, name) in names {
        let xml = ooxml::read_entry(&mut zip, &name)?.unwrap_or_default();
        slides.push(slide_text(&xml)?);
    }
    Ok(slides)
}

pub fn slide_text(xml: &str) -> Result<String> {
    let mut reader = Reader::from_str(xml);
    let mut out = String::new();
    let mut in_text = false;
    loop {
        match reader.read_event()? {
            Event::Start(e) if e.name().as_ref() == b"a:t" => in_text = true,
            Event::End(e) => match e.name().as_ref() {
                b"a:t" => in_text = false,
                b"a:p" => out.push('\n'),
                _ => {}
            },
            Event::Empty(e) if e.name().as_ref() == b"a:br" => out.push('\n'),
            Event::Text(t) if in_text => out.push_str(&t.unescape()?),
            Event::Eof => break,
            _ => {}
        }
    }
    // Collapse the blank lines left by empty paragraphs.
    let lines: Vec<&str> = out.lines().filter(|l| !l.trim().is_empty()).collect();
    Ok(lines.join("\n"))
}

#[cfg(test)]
mod tests {
    #[test]
    fn extracts_paragraphs() {
        let xml = r#"<p:sld><p:txBody><a:p><a:r><a:t>안녕</a:t></a:r><a:r><a:t>하세요</a:t></a:r></a:p><a:p/><a:p><a:r><a:t>둘째 줄</a:t></a:r></a:p></p:txBody></p:sld>"#;
        assert_eq!(super::slide_text(xml).unwrap(), "안녕하세요\n둘째 줄");
    }
}
