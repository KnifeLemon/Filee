//! Raster images via `image`, SVG via `resvg`.

use std::path::Path;

use anyhow::{bail, Context, Result};
use image::imageops::FilterType;
use image::{DynamicImage, ImageFormat, Rgb, RgbImage};

pub fn convert(input: &Path, to: &str, out: &Path) -> Result<()> {
    let img = image::open(input).with_context(|| format!("cannot decode {}", input.display()))?;
    save(img, to, out)
}

pub fn save(img: DynamicImage, to: &str, out: &Path) -> Result<()> {
    let (img, format) = match to {
        "png" => (img, ImageFormat::Png),
        // JPEG has no alpha: composite onto white instead of letting transparent pixels go black.
        "jpg" => (DynamicImage::ImageRgb8(flatten_on_white(&img)), ImageFormat::Jpeg),
        "webp" => (DynamicImage::ImageRgba8(img.to_rgba8()), ImageFormat::WebP),
        "bmp" => (DynamicImage::ImageRgba8(img.to_rgba8()), ImageFormat::Bmp),
        "ico" => {
            let img = if img.width() > 256 || img.height() > 256 {
                img.resize(256, 256, FilterType::Lanczos3)
            } else {
                img
            };
            (DynamicImage::ImageRgba8(img.to_rgba8()), ImageFormat::Ico)
        }
        other => bail!("image engine cannot write {other}"),
    };
    img.save_with_format(out, format).with_context(|| format!("cannot write {}", out.display()))
}

fn flatten_on_white(img: &DynamicImage) -> RgbImage {
    let rgba = img.to_rgba8();
    RgbImage::from_fn(rgba.width(), rgba.height(), |x, y| {
        let [r, g, b, a] = rgba.get_pixel(x, y).0;
        let a = a as u32;
        let blend = |c: u8| ((c as u32 * a + 255 * (255 - a)) / 255) as u8;
        Rgb([blend(r), blend(g), blend(b)])
    })
}

pub fn svg_to_png(input: &Path, out: &Path) -> Result<()> {
    use resvg::{tiny_skia, usvg};

    let data = std::fs::read(input).with_context(|| format!("cannot read {}", input.display()))?;
    let mut opt = usvg::Options::default();
    opt.fontdb_mut().load_system_fonts();
    let tree = usvg::Tree::from_data(&data, &opt).with_context(|| format!("invalid SVG {}", input.display()))?;
    let size = tree.size().to_int_size();
    let mut pixmap = tiny_skia::Pixmap::new(size.width(), size.height()).context("SVG has zero size")?;
    resvg::render(&tree, tiny_skia::Transform::default(), &mut pixmap.as_mut());
    pixmap.save_png(out).with_context(|| format!("cannot write {}", out.display()))
}
