//! End-to-end checks for the pure-Rust engines and the job queue.

use std::io::Write;
use std::path::{Path, PathBuf};

use filee_lite::engines::{self, Engine, Tools};
use filee_lite::{ConvertRequest, JobEvent, JobQueue};

async fn run(engine: Engine, input: &Path, from: &str, to: &str, out: &Path) -> Vec<PathBuf> {
    engines::run(engine, &Tools::default(), input, from, to, out).await.unwrap()
}

fn write_zip(path: &Path, entries: &[(&str, &str)]) {
    let mut zip = zip::ZipWriter::new(std::fs::File::create(path).unwrap());
    for (name, body) in entries {
        zip.start_file(*name, zip::write::SimpleFileOptions::default()).unwrap();
        zip.write_all(body.as_bytes()).unwrap();
    }
    zip.finish().unwrap();
}

#[tokio::test]
async fn csv_xlsx_roundtrip() {
    let dir = tempfile::tempdir().unwrap();
    let csv = dir.path().join("표.csv");
    std::fs::write(&csv, "이름,점수,코드\n홍길동,95,007\n김철수,88.5,010\n").unwrap();

    let xlsx = run(Engine::Spreadsheet, &csv, "csv", "xlsx", &dir.path().join("a")).await;
    assert_eq!(xlsx.len(), 1);
    let back = run(Engine::Spreadsheet, &xlsx[0], "xlsx", "csv", &dir.path().join("b")).await;
    let text = std::fs::read_to_string(&back[0]).unwrap();
    assert_eq!(text.trim_start_matches('\u{feff}'), "이름,점수,코드\n홍길동,95,007\n김철수,88.5,010\n");

    let json = run(Engine::Spreadsheet, &xlsx[0], "xlsx", "json", &dir.path().join("c")).await;
    let v: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(&json[0]).unwrap()).unwrap();
    assert_eq!(v["sheets"][0]["rows"][1][1], 95.0);
    assert_eq!(v["sheets"][0]["rows"][1][2], "007");
}

#[tokio::test]
async fn raster_conversions() {
    let dir = tempfile::tempdir().unwrap();
    let png = dir.path().join("dot.png");
    image::RgbaImage::from_pixel(300, 200, image::Rgba([255, 0, 0, 128])).save(&png).unwrap();

    for to in ["jpg", "webp", "bmp", "ico"] {
        let out = run(Engine::Raster, &png, "png", to, &dir.path().join(to)).await;
        let img = image::open(&out[0]).unwrap();
        if to == "ico" {
            assert!(img.width() <= 256 && img.height() <= 256);
        } else {
            assert_eq!((img.width(), img.height()), (300, 200));
        }
    }
    // Half-transparent red on white, not on black.
    let jpg = image::open(dir.path().join("jpg/dot.jpg")).unwrap().to_rgb8();
    assert!(jpg.get_pixel(10, 10).0[1] > 100);
}

#[tokio::test]
async fn svg_to_png() {
    let dir = tempfile::tempdir().unwrap();
    let svg = dir.path().join("logo.svg");
    std::fs::write(&svg, r#"<svg xmlns="http://www.w3.org/2000/svg" width="40" height="20"><rect width="40" height="20" fill="blue"/></svg>"#).unwrap();
    let out = run(Engine::Svg, &svg, "svg", "png", dir.path()).await;
    let img = image::open(&out[0]).unwrap().to_rgba8();
    assert_eq!(img.dimensions(), (40, 20));
    assert_eq!(img.get_pixel(5, 5).0, [0, 0, 255, 255]);
}

#[tokio::test]
async fn docx_and_pptx_text() {
    let dir = tempfile::tempdir().unwrap();
    let docx = dir.path().join("memo.docx");
    write_zip(&docx, &[
        ("word/styles.xml", r#"<w:styles><w:style w:styleId="1"><w:name w:val="heading 1"/></w:style></w:styles>"#),
        ("word/document.xml", r#"<w:document><w:body><w:p><w:pPr><w:pStyle w:val="1"/></w:pPr><w:r><w:t>회의록</w:t></w:r></w:p><w:p><w:r><w:t>본문</w:t></w:r></w:p></w:body></w:document>"#),
    ]);
    let md = run(Engine::WordText, &docx, "docx", "md", dir.path()).await;
    assert_eq!(std::fs::read_to_string(&md[0]).unwrap(), "# 회의록\n\n본문\n\n");

    let pptx = dir.path().join("deck.pptx");
    write_zip(&pptx, &[
        ("ppt/slides/slide10.xml", r#"<p:sld><a:p><a:r><a:t>열</a:t></a:r></a:p></p:sld>"#),
        ("ppt/slides/slide2.xml", r#"<p:sld><a:p><a:r><a:t>둘</a:t></a:r></a:p></p:sld>"#),
    ]);
    let txt = run(Engine::PptText, &pptx, "pptx", "txt", dir.path()).await;
    assert_eq!(std::fs::read_to_string(&txt[0]).unwrap(), "--- Slide 1 ---\n둘\n\n--- Slide 2 ---\n열\n");
}

#[tokio::test]
async fn markdown_to_html() {
    let dir = tempfile::tempdir().unwrap();
    let md = dir.path().join("doc.md");
    std::fs::write(&md, "# 제목\n\n| a | b |\n|---|---|\n| 1 | 2 |\n").unwrap();
    let out = run(Engine::Markdown, &md, "md", "html", dir.path()).await;
    let html = std::fs::read_to_string(&out[0]).unwrap();
    assert!(html.contains("<h1>제목</h1>"));
    assert!(html.contains("<table>"));
    assert!(html.contains("<title>doc</title>"));
}

#[tokio::test]
async fn queue_converts_multiple_files_without_overwriting() {
    let dir = tempfile::tempdir().unwrap();
    let a = dir.path().join("a.png");
    let b = dir.path().join("b.gif");
    image::RgbImage::new(8, 8).save(&a).unwrap();
    image::RgbImage::new(8, 8).save(&b).unwrap();
    std::fs::write(dir.path().join("a.bmp"), b"existing").unwrap();

    let (queue, mut events) = JobQueue::with_concurrency(Tools::default(), 2);
    let job = queue
        .submit(ConvertRequest { files: vec![a, b], target: "bmp".into(), output_dir: None })
        .unwrap();

    let mut last_progress = 0.0;
    loop {
        match events.recv().await.unwrap() {
            JobEvent::Progress { progress, job_id } => {
                assert_eq!(job_id, job);
                assert!(progress >= last_progress);
                last_progress = progress;
            }
            JobEvent::Done { output_paths, .. } => {
                let mut names: Vec<String> = output_paths
                    .iter()
                    .map(|p| Path::new(p).file_name().unwrap().to_string_lossy().into_owned())
                    .collect();
                names.sort();
                assert_eq!(names, ["a (1).bmp", "b.bmp"]);
                break;
            }
            other => panic!("unexpected {other:?}"),
        }
    }
    assert_eq!(last_progress, 1.0);
    assert_eq!(std::fs::read(dir.path().join("a.bmp")).unwrap(), b"existing");
}

#[tokio::test]
async fn queue_rejects_unroutable_requests() {
    let (queue, _events) = JobQueue::new(Tools::default());
    let err = queue
        .submit(ConvertRequest { files: vec!["x.png".into()], target: "mp3".into(), output_dir: None })
        .unwrap_err();
    assert!(err.to_string().contains("no conversion route"));
    assert!(queue.cancel("job-404").is_err());
}
