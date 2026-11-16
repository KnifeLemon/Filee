//! Markdown -> HTML via `pulldown-cmark`.

use std::path::Path;

use anyhow::{bail, Context, Result};
use pulldown_cmark::{html, Options, Parser};

pub fn convert(input: &Path, to: &str, out: &Path) -> Result<()> {
    if to != "html" {
        bail!("markdown engine cannot write {to}");
    }
    let md = std::fs::read_to_string(input).with_context(|| format!("cannot read {}", input.display()))?;
    std::fs::write(out, render_html(&md, &super::stem(input)))
        .with_context(|| format!("cannot write {}", out.display()))
}

/// Standalone HTML page. The stylesheet matters because LibreOffice prints this to PDF.
pub fn render_html(md: &str, title: &str) -> String {
    let opts = Options::ENABLE_TABLES
        | Options::ENABLE_STRIKETHROUGH
        | Options::ENABLE_TASKLISTS
        | Options::ENABLE_FOOTNOTES;
    let mut body = String::with_capacity(md.len() * 2);
    html::push_html(&mut body, Parser::new_ext(md, opts));
    format!(
        "<!DOCTYPE html>\n<html lang=\"ko\">\n<head>\n<meta charset=\"utf-8\">\n<title>{}</title>\n<style>\n\
body{{font-family:'Malgun Gothic','Apple SD Gothic Neo','Noto Sans KR',sans-serif;line-height:1.6;max-width:48em;margin:2em auto;padding:0 1em}}\n\
table{{border-collapse:collapse}}th,td{{border:1px solid #999;padding:.25em .5em}}\n\
img{{max-width:100%}}\n</style>\n</head>\n<body>\n{body}</body>\n</html>\n",
        escape(title)
    )
}

fn escape(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;")
}
