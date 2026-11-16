//! DOCX -> text / Markdown by reading `word/document.xml` directly.

use std::collections::HashMap;
use std::path::Path;

use anyhow::{bail, Context, Result};
use quick_xml::events::Event;
use quick_xml::Reader;

use super::ooxml;

#[derive(Debug, Default, PartialEq)]
pub struct Paragraph {
    pub heading: Option<u8>,
    pub list: bool,
    pub text: String,
}

pub fn convert(input: &Path, to: &str, out: &Path) -> Result<()> {
    let paragraphs = read_paragraphs(input)?;
    let content = match to {
        "txt" => paragraphs.iter().map(|p| p.text.as_str()).collect::<Vec<_>>().join("\n"),
        "md" => to_markdown(&paragraphs),
        other => bail!("docx text engine cannot write {other}"),
    };
    std::fs::write(out, content).with_context(|| format!("cannot write {}", out.display()))
}

pub fn read_paragraphs(input: &Path) -> Result<Vec<Paragraph>> {
    let mut zip = ooxml::open(input)?;
    let document = ooxml::read_entry(&mut zip, "word/document.xml")?
        .context("word/document.xml missing (not a DOCX file?)")?;
    let headings = match ooxml::read_entry(&mut zip, "word/styles.xml")? {
        Some(styles) => heading_styles(&styles)?,
        None => HashMap::new(),
    };
    parse_document(&document, &headings)
}

pub fn to_markdown(paragraphs: &[Paragraph]) -> String {
    let mut out = String::new();
    for p in paragraphs {
        let text = p.text.trim_end();
        if text.is_empty() {
            continue;
        }
        if let Some(level) = p.heading {
            out.push_str(&"#".repeat(level.clamp(1, 6) as usize));
            out.push(' ');
        } else if p.list {
            out.push_str("- ");
        }
        out.push_str(&text.replace('\n', "  \n"));
        out.push_str(if p.list { "\n" } else { "\n\n" });
    }
    out
}

/// styleId -> heading level. Style ids are localized (Korean Word uses "1", "2", ...),
/// so the level comes from the style's English `w:name` ("heading 1", "Title").
fn heading_styles(xml: &str) -> Result<HashMap<String, u8>> {
    let mut map = HashMap::new();
    let mut reader = Reader::from_str(xml);
    let mut current: Option<String> = None;
    loop {
        match reader.read_event()? {
            Event::Start(e) if e.name().as_ref() == b"w:style" => current = ooxml::attr(&e, "w:styleId")?,
            Event::Empty(e) if e.name().as_ref() == b"w:name" => {
                if let (Some(id), Some(name)) = (current.as_ref(), ooxml::attr(&e, "w:val")?) {
                    if let Some(level) = heading_level(&name) {
                        map.insert(id.clone(), level);
                    }
                }
            }
            Event::End(e) if e.name().as_ref() == b"w:style" => current = None,
            Event::Eof => break,
            _ => {}
        }
    }
    Ok(map)
}

fn heading_level(style_name: &str) -> Option<u8> {
    let name = style_name.to_ascii_lowercase();
    if name == "title" {
        return Some(1);
    }
    name.strip_prefix("heading").and_then(|n| n.trim().parse().ok()).filter(|n| (1..=9).contains(n))
}

pub fn parse_document(xml: &str, headings: &HashMap<String, u8>) -> Result<Vec<Paragraph>> {
    let mut reader = Reader::from_str(xml);
    let mut done = Vec::new();
    // Text boxes nest paragraphs inside paragraphs, hence a stack.
    let mut stack: Vec<Paragraph> = Vec::new();
    let mut in_text = false;
    loop {
        match reader.read_event()? {
            Event::Start(e) => match e.name().as_ref() {
                b"w:p" => stack.push(Paragraph::default()),
                b"w:t" => in_text = true,
                b"w:numPr" => set(&mut stack, |p| p.list = true),
                _ => {}
            },
            Event::Empty(e) => match e.name().as_ref() {
                b"w:p" => done.push(Paragraph::default()),
                b"w:pStyle" => {
                    let level = ooxml::attr(&e, "w:val")?.and_then(|id| {
                        headings.get(&id).copied().or_else(|| heading_level(&id))
                    });
                    set(&mut stack, |p| p.heading = level);
                }
                b"w:tab" => set(&mut stack, |p| p.text.push('\t')),
                b"w:br" | b"w:cr" => set(&mut stack, |p| p.text.push('\n')),
                _ => {}
            },
            Event::Text(t) if in_text => {
                let text = t.unescape()?;
                set(&mut stack, |p| p.text.push_str(&text));
            }
            Event::End(e) => match e.name().as_ref() {
                b"w:t" => in_text = false,
                b"w:p" => done.extend(stack.pop()),
                _ => {}
            },
            Event::Eof => break,
            _ => {}
        }
    }
    Ok(done)
}

fn set(stack: &mut [Paragraph], f: impl FnOnce(&mut Paragraph)) {
    if let Some(p) = stack.last_mut() {
        f(p);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_headings_lists_and_breaks() {
        let xml = r#"<w:document><w:body>
            <w:p><w:pPr><w:pStyle w:val="1"/></w:pPr><w:r><w:t>제목</w:t></w:r></w:p>
            <w:p><w:pPr><w:numPr><w:ilvl w:val="0"/></w:numPr></w:pPr><w:r><w:t xml:space="preserve">항목 &amp; 하나</w:t></w:r></w:p>
            <w:p><w:r><w:t>a</w:t><w:br/><w:t>b</w:t></w:r></w:p>
        </w:body></w:document>"#;
        let headings = HashMap::from([("1".to_string(), 1u8)]);
        let ps = parse_document(xml, &headings).unwrap();
        assert_eq!(ps.len(), 3);
        assert_eq!(ps[0].heading, Some(1));
        assert_eq!(ps[1].text, "항목 & 하나");
        assert!(ps[1].list);
        assert_eq!(to_markdown(&ps), "# 제목\n\n- 항목 & 하나\na  \nb\n\n");
    }

    #[test]
    fn heading_names() {
        assert_eq!(heading_level("heading 2"), Some(2));
        assert_eq!(heading_level("Heading1"), Some(1));
        assert_eq!(heading_level("Normal"), None);
    }
}
