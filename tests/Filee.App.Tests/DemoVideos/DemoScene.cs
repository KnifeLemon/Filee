// The made-up desktop the demo videos play on: a soft background, a plain file manager window with a few files, a
// mouse pointer, the file being dragged and a keycap. Generic on purpose: no operating system's or app's look, no
// real names or logos. Colours are Filee's own (Styles/Soft.axaml and the theme accents).

using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Filee.App.Tests.DemoVideos;

/// <summary>Colours of the scene around Filee's donut, light or dark like Filee's theme.</summary>
internal sealed record DemoPalette(
    Color DesktopTop, Color DesktopBottom, Color Glow, Color Window, Color Chrome, Color Line, Color Text, Color Muted,
    Color Accent, Color Selection, Color Field, Color Paper, double Shadow)
{
    public static readonly DemoPalette Light = new(
        DesktopTop: Color.Parse("#ECEAF8"), DesktopBottom: Color.Parse("#F6F5F1"), Glow: Color.Parse("#D9F0E7"),
        Window: Color.Parse("#FFFFFF"), Chrome: Color.Parse("#F6F5F1"), Line: Color.Parse("#16000000"),
        Text: Color.Parse("#1F1E1D"), Muted: Color.Parse("#6B6A66"), Accent: Color.Parse("#7F77DD"),
        Selection: Color.Parse("#2E7F77DD"), Field: Color.Parse("#EFEDE6"), Paper: Color.Parse("#FFFFFF"), Shadow: 0.16);

    public static readonly DemoPalette Dark = new(
        DesktopTop: Color.Parse("#2A2552"), DesktopBottom: Color.Parse("#151513"), Glow: Color.Parse("#173B31"),
        Window: Color.Parse("#262624"), Chrome: Color.Parse("#1F1F1D"), Line: Color.Parse("#1CFFFFFF"),
        Text: Color.Parse("#F1EFE8"), Muted: Color.Parse("#AEACA4"), Accent: Color.Parse("#7F77DD"),
        Selection: Color.Parse("#4D7F77DD"), Field: Color.Parse("#31312E"), Paper: Color.Parse("#ECEAE3"), Shadow: 0.5);
}

/// <summary>
/// One framing of a clip: its size in layout units, how many pixels a unit is, and where the window sits. The
/// square and vertical framings are for social video, the wide one for animated images on pages and slides.
/// </summary>
internal sealed record DemoFormat(string Name, double Width, double Height, double Scale, Rect Window, double Sidebar, Point KeyCap, int Columns = 4)
{
    public const double TitleBar = 40;
    public const double Toolbar = 44;
    public const double CellWidth = 108;
    public const double CellHeight = 124;

    public static readonly DemoFormat Square = new("square", 720, 720, 1.5, new Rect(48, 96, 624, 528), 148, new Point(360, 672));
    public static readonly DemoFormat Vertical = new("vertical", 540, 960, 2.0, new Rect(20, 196, 500, 604), 120, new Point(270, 884), Columns: 3);
    public static readonly DemoFormat Wide = new("wide", 1024, 576, 1.25, new Rect(200, 30, 640, 516), 148, new Point(100, 530));

    public PixelSize Pixels => new((int)Math.Round(Width * Scale), (int)Math.Round(Height * Scale));

    /// <summary>Middle of a file's icon in grid slot <paramref name="slot"/> (<see cref="Columns"/> to a row).</summary>
    public Point Slot(int slot) => Slot(Window, Sidebar, Columns, slot);

    /// <summary>Middle of a file's icon in grid slot <paramref name="slot"/> of a file window at <paramref name="window"/>.</summary>
    public static Point Slot(Rect window, double sidebar, int columns, int slot) => new(
        window.X + sidebar + 20 + CellWidth / 2 + slot % columns * CellWidth,
        window.Y + TitleBar + Toolbar + 22 + 36 + slot / columns * CellHeight);
}

/// <summary>A file in the made-up file manager.</summary>
internal sealed record DemoFile(string Name, string Badge, Color BadgeColor, bool IsPhoto = false, bool Lake = false, bool Cutout = false);

/// <summary>Draws the scene. Everything is in layout units; the caller scales to pixels.</summary>
internal sealed class DemoPainter(DemoPalette palette, FontFamily font)
{
    private readonly Typeface _regular = new(font);
    private readonly Typeface _semibold = new(font, FontStyle.Normal, FontWeight.SemiBold);

    public DemoPalette Palette => palette;

    private static SolidColorBrush Brush(Color color, double opacity = 1) => new(color, opacity);

    private FormattedText Text(string text, double size, Color color, bool bold = false, double opacity = 1) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? _semibold : _regular, size, Brush(color, opacity));

    /// <summary>The desktop: a quiet gradient with a soft glow of Filee's green, like its banners.</summary>
    public void Desktop(DrawingContext dc, DemoFormat format)
    {
        var bounds = new Rect(0, 0, format.Width, format.Height);
        dc.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.4, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(palette.DesktopTop, 0), new GradientStop(palette.DesktopBottom, 1) },
        }, null, bounds);
        var glow = new RadialGradientBrush
        {
            GradientStops = { new GradientStop(palette.Glow, 0), new GradientStop(Color.FromArgb(0, palette.Glow.R, palette.Glow.G, palette.Glow.B), 1) },
        };
        var radius = Math.Max(format.Width, format.Height) * 0.55;
        using (dc.PushOpacity(0.8))
            dc.DrawEllipse(glow, null, new Point(format.Width * 0.92, format.Height * 0.95), radius, radius);
    }

    /// <summary>
    /// A plain file manager window: a title bar, a toolbar with a path and a search field, a sidebar of places and a grid
    /// of files (drawn on it by <see cref="File"/>), and a status line (<see cref="StatusLine"/>).
    /// </summary>
    public void FileManager(DrawingContext dc, Rect w, double sidebar, string parent, string folder, IReadOnlyList<string> places, int place)
    {
        WindowFrame(dc, w);

        using (dc.PushClip(new RoundedRect(w, 14)))
        {
            // Title bar and toolbar share the chrome colour; the sidebar too, the files sit on the window colour.
            var chrome = new Rect(w.X, w.Y, w.Width, DemoFormat.TitleBar + DemoFormat.Toolbar);
            dc.DrawRectangle(Brush(palette.Chrome), null, chrome);
            dc.DrawRectangle(Brush(palette.Chrome), null, new Rect(w.X, chrome.Bottom, sidebar, w.Height - chrome.Height));
            dc.DrawLine(new Pen(Brush(palette.Line), 1), new Point(w.X, chrome.Bottom), new Point(w.Right, chrome.Bottom));
            if (sidebar > 0)
                dc.DrawLine(new Pen(Brush(palette.Line), 1), new Point(w.X + sidebar, chrome.Bottom), new Point(w.X + sidebar, w.Bottom));

            // Title: a folder and its name; three neutral window buttons.
            Folder(dc, new Rect(w.X + 18, w.Y + 13, 18, 14), palette.Accent);
            dc.DrawText(Text(folder, 13.5, palette.Text, bold: true), new Point(w.X + 44, w.Y + 11));
            for (var i = 0; i < 3; i++)
                dc.DrawEllipse(Brush(palette.Muted, 0.35), null, new Point(w.Right - 22 - i * 20, w.Y + 20), 5, 5);

            // Toolbar: back and forward, the path, a search field.
            var y = w.Y + DemoFormat.TitleBar + DemoFormat.Toolbar / 2;
            var arrow = new Pen(Brush(palette.Muted), 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            Chevron(dc, new Point(w.X + 24, y), -1, arrow);
            Chevron(dc, new Point(w.X + 48, y), 1, new Pen(Brush(palette.Muted, 0.45), 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
            var searchWidth = Math.Min(170, w.Width * 0.26);
            var path = new Rect(w.X + 70, y - 14, w.Width - 70 - searchWidth - 26, 28);
            dc.DrawRectangle(Brush(palette.Field), null, new RoundedRect(path, 8));
            var crumb = Text(parent + "  ›  " + folder, 12.5, palette.Text);
            dc.DrawText(crumb, new Point(path.X + 12, y - crumb.Height / 2));
            var search = new Rect(path.Right + 12, y - 14, searchWidth, 28);
            dc.DrawRectangle(Brush(palette.Field), null, new RoundedRect(search, 8));
            var lens = new Pen(Brush(palette.Muted), 1.6, lineCap: PenLineCap.Round);
            dc.DrawEllipse(null, lens, new Point(search.X + 16, y - 1.5), 5, 5);
            dc.DrawLine(lens, new Point(search.X + 19.6, y + 2.1), new Point(search.X + 23, y + 5.5));

            // Sidebar: places, the open one marked.
            for (var i = 0; i < places.Count; i++)
            {
                var row = new Rect(w.X + 10, chrome.Bottom + 14 + i * 34, sidebar - 20, 28);
                if (i == place)
                    dc.DrawRectangle(Brush(palette.Selection), null, new RoundedRect(row, 8));
                Folder(dc, new Rect(row.X + 10, row.Y + 8, 15, 12), i == place ? palette.Accent : palette.Muted);
                var label = Text(places[i], 12.5, palette.Text, bold: i == place);
                dc.DrawText(label, new Point(row.X + 34, row.Center.Y - label.Height / 2));
            }
        }
    }

    /// <summary>A window's body and its soft shadow.</summary>
    private void WindowFrame(DrawingContext dc, Rect w)
    {
        var shadow = new BoxShadows(new BoxShadow
        {
            OffsetY = 18,
            Blur = 48,
            Color = Color.FromArgb((byte)(255 * palette.Shadow), 20, 16, 40),
        });
        dc.DrawRectangle(Brush(palette.Window), new Pen(Brush(palette.Line), 1), new RoundedRect(w, 14), shadow);
    }

    /// <summary>Height of a browser window's tab strip and address bar.</summary>
    public const double BrowserBar = 84;

    /// <summary>
    /// A plain web browser: one tab, an address bar, and a made-up photo page (a site header, a picture, its title and
    /// a few lines of text). The address uses the reserved ".example" domain, so it is nobody's site.
    /// </summary>
    public void Browser(DrawingContext dc, Rect w, string tab, string address, string site, string title)
    {
        WindowFrame(dc, w);
        using (dc.PushClip(new RoundedRect(w, 14)))
        {
            dc.DrawRectangle(Brush(palette.Chrome), null, new Rect(w.X, w.Y, w.Width, BrowserBar));
            dc.DrawLine(new Pen(Brush(palette.Line), 1), new Point(w.X, w.Y + BrowserBar), new Point(w.Right, w.Y + BrowserBar));

            // The tab: a little site mark and the page's name, on the window colour like the page below it.
            var tabRect = new Rect(w.X + 12, w.Y + 8, Math.Min(230, w.Width * 0.45), 32);
            dc.DrawRectangle(Brush(palette.Window), null, new RoundedRect(tabRect, 9, 9, 0, 0));
            dc.DrawRectangle(Brush(palette.Accent), null, new RoundedRect(new Rect(tabRect.X + 12, tabRect.Center.Y - 7, 14, 14), 4));
            var tabText = Text(tab, 12.5, palette.Text);
            using (dc.PushClip(new Rect(tabRect.X + 34, tabRect.Y, tabRect.Width - 44, tabRect.Height)))
                dc.DrawText(tabText, new Point(tabRect.X + 34, tabRect.Center.Y - tabText.Height / 2));
            var plus = new Pen(Brush(palette.Muted), 1.6, lineCap: PenLineCap.Round);
            var p = new Point(tabRect.Right + 18, tabRect.Center.Y);
            dc.DrawLine(plus, p - new Point(5, 0), p + new Point(5, 0));
            dc.DrawLine(plus, p - new Point(0, 5), p + new Point(0, 5));
            for (var i = 0; i < 3; i++)
                dc.DrawEllipse(Brush(palette.Muted, 0.35), null, new Point(w.Right - 22 - i * 20, w.Y + 24), 5, 5);

            // Address bar: back, forward, reload and the address.
            var y = w.Y + 40 + 22;
            var arrow = new Pen(Brush(palette.Muted), 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            Chevron(dc, new Point(w.X + 24, y), -1, arrow);
            Chevron(dc, new Point(w.X + 48, y), 1, new Pen(Brush(palette.Muted, 0.45), 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
            var reload = new StreamGeometry();
            using (var g = reload.Open())
            {
                g.BeginFigure(new Point(w.X + 78, y - 6), false);
                g.ArcTo(new Point(w.X + 71.5, y + 3.5), new Size(6.5, 6.5), 0, true, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            dc.DrawGeometry(null, arrow, reload);
            var bar = new Rect(w.X + 96, y - 14, w.Width - 96 - 16, 28);
            dc.DrawRectangle(Brush(palette.Field), null, new RoundedRect(bar, 14));
            var url = Text(address, 12.5, palette.Muted);
            dc.DrawText(url, new Point(bar.X + 16, y - url.Height / 2));

            // The page: a site header, the picture, its title and some text.
            var page = new Rect(w.X, w.Y + BrowserBar, w.Width, w.Height - BrowserBar);
            dc.DrawRectangle(Brush(palette.Text, 0.03), null, new Rect(page.X, page.Y, page.Width, 46));
            dc.DrawRectangle(Brush(Color.Parse("#1D9E75")), null, new RoundedRect(new Rect(page.X + 24, page.Y + 14, 18, 18), 5));
            dc.DrawText(Text(site, 14, palette.Text, bold: true), new Point(page.X + 50, page.Y + 13));
            for (var i = 0; i < 3; i++)
                dc.DrawRectangle(Brush(palette.Muted, 0.35), null, new RoundedRect(new Rect(page.Right - 64 - i * 62, page.Y + 20, 44, 6), 3));
            var picture = BrowserPicture(w);
            Photo(dc, picture, 12, lake: true);
            var heading = Text(title, 17, palette.Text, bold: true);
            dc.DrawText(heading, new Point(picture.X, picture.Bottom + 16));
            var lines = picture.Bottom + 22 + heading.Height;
            for (var i = 0; i < 3 && lines + i * 14 < page.Bottom - 16; i++)
                dc.DrawRectangle(Brush(palette.Muted, 0.28), null,
                    new RoundedRect(new Rect(picture.X, lines + i * 14, i == 2 ? picture.Width * 0.55 : picture.Width, 6), 3));
        }
    }

    /// <summary>Where the picture is on the page of a browser window at <paramref name="w"/>.</summary>
    public static Rect BrowserPicture(Rect w)
    {
        var width = Math.Min(380, w.Width - 96);
        var height = Math.Round(width * 0.6);
        return new Rect(w.X + (w.Width - width) / 2, w.Y + BrowserBar + 46 + 24, width, height);
    }

    /// <summary>How many files the window shows, at its bottom.</summary>
    public void StatusLine(DrawingContext dc, Rect w, double sidebar, int items)
    {
        var status = Text(items == 1 ? "1 item" : $"{items} items", 11.5, palette.Muted);
        dc.DrawText(status, new Point(w.X + sidebar + 20, w.Bottom - 12 - status.Height));
    }

    /// <summary>Selection (or hover) behind a file's icon and name: <paramref name="amount"/> 0 to 1.</summary>
    public void FileHighlight(DrawingContext dc, Point icon, double amount)
    {
        if (amount <= 0.01)
            return;
        var box = new Rect(icon.X - 48, icon.Y - 44, 96, 108);
        dc.DrawRectangle(Brush(palette.Selection, amount), new Pen(Brush(palette.Accent, 0.5 * amount), 1), new RoundedRect(box, 10));
    }

    /// <summary>A file's icon centred on <paramref name="icon"/>, its name below.</summary>
    public void File(DrawingContext dc, Point icon, DemoFile file, bool label = true)
    {
        if (file.IsPhoto)
            Photo(dc, new Rect(icon.X - 30, icon.Y - 30, 60, 60), 10, file.Lake, file.Cutout);
        else
            Page(dc, new Rect(icon.X - 24, icon.Y - 32, 48, 62));
        if (file.Badge.Length > 0)
            Badge(dc, new Point(icon.X - (file.IsPhoto ? 31 : 27), icon.Y + 12), file.Badge, file.BadgeColor);
        if (!label)
            return;
        var name = Text(file.Name, 12, palette.Text);
        dc.DrawText(name, new Point(icon.X - name.Width / 2, icon.Y + 40));
    }

    /// <summary>A photo of hills under the sun (by a lake; or the hills alone on a see-through background).</summary>
    public void Photo(DrawingContext dc, Rect r, double corner, bool lake = false, bool cutout = false)
    {
        if (cutout)
        {
            using (dc.PushClip(new RoundedRect(r, corner)))
            {
                dc.DrawRectangle(Brush(Colors.White), null, r);
                const int cells = 6;
                var size = r.Width / cells;
                for (var y = 0; y < cells; y++)
                    for (var x = (y % 2); x < cells; x += 2)
                        dc.DrawRectangle(Brush(Color.Parse("#DCDAE6")), null, new Rect(r.X + x * size, r.Y + y * size, size, size));
                Hill(dc, r, 0.15, 0.48, 0.95, Color.Parse("#6BAF7C"));
                Hill(dc, r, 0.55, 0.38, 1.25, Color.Parse("#3F8F6B"));
                dc.DrawRectangle(Brush(Color.Parse("#2F7A5A")), null, new Rect(r.X, r.Bottom - r.Height * 0.14, r.Width, r.Height * 0.14));
            }
            dc.DrawRectangle(null, new Pen(Brush(Colors.Black, 0.08), 1), new RoundedRect(r, corner));
            return;
        }
        if (lake)
        {
            using (dc.PushClip(new RoundedRect(r, corner)))
            {
                dc.DrawRectangle(new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#F6C9A8"), 0), new GradientStop(Color.Parse("#FBE7D3"), 0.55) },
                }, null, r);
                dc.DrawEllipse(Brush(Color.Parse("#FFF1C9")), null, new Point(r.X + r.Width * 0.68, r.Y + r.Height * 0.4), r.Width * 0.07, r.Width * 0.07);
                Hill(dc, r, 0.2, 0.3, 1.0, Color.Parse("#8C8FC4"), 0.62);
                Hill(dc, r, 0.75, 0.36, 0.9, Color.Parse("#6F72B0"), 0.62);
                var water = new Rect(r.X, r.Y + r.Height * 0.62, r.Width, r.Height * 0.38);
                dc.DrawRectangle(new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#7E9BD1"), 0), new GradientStop(Color.Parse("#4F6DAF"), 1) },
                }, null, water);
                for (var i = 0; i < 3; i++)
                    dc.DrawRectangle(Brush(Colors.White, 0.35), null,
                        new RoundedRect(new Rect(r.X + r.Width * (0.55 + i * 0.05), water.Y + water.Height * (0.2 + i * 0.22), r.Width * (0.24 - i * 0.06), Math.Max(1, r.Height * 0.012)), 1));
            }
            dc.DrawRectangle(null, new Pen(Brush(Colors.Black, 0.08), 1), new RoundedRect(r, corner));
            return;
        }
        using (dc.PushClip(new RoundedRect(r, corner)))
        {
            dc.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#9CC9F2"), 0), new GradientStop(Color.Parse("#D9ECFA"), 0.7) },
            }, null, r);
            dc.DrawEllipse(Brush(Color.Parse("#F7C84B")), null, new Point(r.X + r.Width * 0.3, r.Y + r.Height * 0.32), r.Width * 0.11, r.Width * 0.11);
            Hill(dc, r, 0.15, 0.48, 0.95, Color.Parse("#6BAF7C"));
            Hill(dc, r, 0.55, 0.38, 1.25, Color.Parse("#3F8F6B"));
            dc.DrawRectangle(Brush(Color.Parse("#2F7A5A")), null, new Rect(r.X, r.Bottom - r.Height * 0.14, r.Width, r.Height * 0.14));
        }
        dc.DrawRectangle(null, new Pen(Brush(Colors.Black, 0.08), 1), new RoundedRect(r, corner));

        static void Hill(DrawingContext dc, Rect r, double peakX, double peakY, double width, Color color, double bottom = 1)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                var px = r.X + r.Width * peakX;
                var py = r.Y + r.Height * peakY;
                var baseY = r.Y + r.Height * bottom;
                c.BeginFigure(new Point(px - r.Width * width / 2, baseY), true);
                c.QuadraticBezierTo(new Point(px, py - r.Height * 0.12), new Point(px + r.Width * width / 2, baseY));
                c.EndFigure(true);
            }
            dc.DrawGeometry(new SolidColorBrush(color), null, g);
        }
    }

    /// <summary>A page with a folded corner and a few lines of text.</summary>
    private void Page(DrawingContext dc, Rect r)
    {
        const double fold = 13;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(r.X + 5, r.Y), true);
            c.LineTo(new Point(r.Right - fold, r.Y));
            c.LineTo(new Point(r.Right, r.Y + fold));
            c.LineTo(new Point(r.Right, r.Bottom - 5));
            c.ArcTo(new Point(r.Right - 5, r.Bottom), new Size(5, 5), 0, false, SweepDirection.Clockwise);
            c.LineTo(new Point(r.X + 5, r.Bottom));
            c.ArcTo(new Point(r.X, r.Bottom - 5), new Size(5, 5), 0, false, SweepDirection.Clockwise);
            c.LineTo(new Point(r.X, r.Y + 5));
            c.ArcTo(new Point(r.X + 5, r.Y), new Size(5, 5), 0, false, SweepDirection.Clockwise);
            c.EndFigure(true);
        }
        dc.DrawGeometry(Brush(palette.Paper), new Pen(Brush(Colors.Black, 0.14), 1), g);
        var corner = new StreamGeometry();
        using (var c = corner.Open())
        {
            c.BeginFigure(new Point(r.Right - fold, r.Y), true);
            c.LineTo(new Point(r.Right - fold, r.Y + fold - 2));
            c.ArcTo(new Point(r.Right - fold + 2, r.Y + fold), new Size(2, 2), 0, false, SweepDirection.CounterClockwise);
            c.LineTo(new Point(r.Right, r.Y + fold));
            c.EndFigure(true);
        }
        dc.DrawGeometry(Brush(Color.Parse("#E1DFEF")), null, corner);
        var line = Brush(Color.Parse("#D3D1E3"));
        for (var i = 0; i < 4; i++)
            dc.DrawRectangle(line, null, new RoundedRect(new Rect(r.X + 9, r.Y + 20 + i * 7, i == 3 ? r.Width * 0.4 : r.Width - 18, 2.6), 1.3));
    }

    /// <summary>The coloured extension tag on a file icon.</summary>
    private void Badge(DrawingContext dc, Point at, string text, Color color)
    {
        var label = Text(text, 9.5, Colors.White, bold: true);
        var box = new Rect(at.X, at.Y, label.Width + 10, 15);
        dc.DrawRectangle(Brush(color), null, new RoundedRect(box, 4));
        dc.DrawText(label, new Point(box.X + 5, box.Center.Y - label.Height / 2));
    }

    private static void Folder(DrawingContext dc, Rect r, Color color)
    {
        dc.DrawRectangle(new SolidColorBrush(color, 0.75), null, new RoundedRect(new Rect(r.X, r.Y, r.Width * 0.45, r.Height * 0.5), 2));
        dc.DrawRectangle(new SolidColorBrush(color), null, new RoundedRect(new Rect(r.X, r.Y + r.Height * 0.2, r.Width, r.Height * 0.8), 2.5));
    }

    private static void Chevron(DrawingContext dc, Point at, int direction, Pen pen)
    {
        dc.DrawLine(pen, new Point(at.X + 3 * direction * -1, at.Y - 6), new Point(at.X + 3 * direction, at.Y));
        dc.DrawLine(pen, new Point(at.X + 3 * direction, at.Y), new Point(at.X + 3 * direction * -1, at.Y + 6));
    }

    /// <summary>The file under the pointer while it is dragged: a small, see-through copy of its icon.</summary>
    public void DragGhost(DrawingContext dc, Point pointer, DemoFile file, double opacity)
    {
        if (opacity <= 0.01)
            return;
        var at = new Point(pointer.X + 30, pointer.Y + 38);
        using (dc.PushOpacity(opacity * 0.7))
        using (dc.PushTransform(Matrix.CreateTranslation(-at.X, -at.Y) * Matrix.CreateScale(0.5, 0.5) * Matrix.CreateTranslation(at.X, at.Y)))
            File(dc, at, file, label: false);
    }

    /// <summary>An arrow pointer with its tip at <paramref name="tip"/>; pressed, it dips a little.</summary>
    public void Pointer(DrawingContext dc, Point tip, double pressed)
    {
        var s = 1.25 * (1 - 0.08 * pressed);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, 0), true);
            c.LineTo(new Point(0, 17.5));
            c.LineTo(new Point(4.2, 13.6));
            c.LineTo(new Point(7.1, 20.3));
            c.LineTo(new Point(10.1, 19));
            c.LineTo(new Point(7.3, 12.5));
            c.LineTo(new Point(12.8, 12.5));
            c.EndFigure(true);
        }
        var place = Matrix.CreateScale(s, s) * Matrix.CreateTranslation(tip.X, tip.Y);
        using (dc.PushTransform(Matrix.CreateTranslation(0.6, 1.6) * place))
            dc.DrawGeometry(Brush(Colors.Black, 0.22), new Pen(Brush(Colors.Black, 0.12), 2.4, lineJoin: PenLineJoin.Round), g);
        using (dc.PushTransform(place))
            dc.DrawGeometry(Brush(Colors.White), new Pen(Brush(Color.Parse("#1F1E1D")), 1.15, lineJoin: PenLineJoin.Round), g);
    }

    /// <summary>A soft ring spreading from a click, <paramref name="age"/> seconds after it.</summary>
    public void Ripple(DrawingContext dc, Point at, double age)
    {
        const double life = 0.45;
        if (age < 0 || age > life)
            return;
        var t = Ease.OutCubic(age / life);
        dc.DrawEllipse(Brush(palette.Accent, 0.22 * (1 - t)), new Pen(Brush(palette.Accent, 0.55 * (1 - t)), 1.6), at, 6 + 20 * t, 6 + 20 * t);
    }

    /// <summary>A keyboard key showing which key is held: <paramref name="down"/> 0 (up) to 1 (pressed).</summary>
    public void KeyCap(DrawingContext dc, Point center, string label, double opacity, double down)
    {
        if (opacity <= 0.01)
            return;
        var text = Text(label, 15, palette.Text, bold: true);
        var width = Math.Max(64, text.Width + 30);
        var lift = 4 * (1 - down);
        var body = new Rect(center.X - width / 2, center.Y - 20, width, 40);
        using (dc.PushOpacity(opacity))
        {
            dc.DrawRectangle(Brush(palette.Field), new Pen(Brush(palette.Line), 1), new RoundedRect(body.Translate(new Vector(0, 2)), 10),
                new BoxShadows(new BoxShadow { OffsetY = 6, Blur = 16, Color = Color.FromArgb((byte)(255 * palette.Shadow * 0.8), 20, 16, 40) }));
            var top = new Rect(body.X, body.Y - lift + 2, body.Width, body.Height - 2);
            dc.DrawRectangle(Brush(palette.Window), new Pen(Brush(down > 0.5 ? palette.Accent : palette.Line, down > 0.5 ? 0.9 : 1), down > 0.5 ? 1.5 : 1),
                new RoundedRect(top, 10));
            dc.DrawText(text, new Point(center.X - text.Width / 2, top.Center.Y - text.Height / 2));
        }
    }
}
