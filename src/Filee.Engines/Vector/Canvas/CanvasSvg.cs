// The SVG a JSON Canvas board is drawn into: a few shapes and text elements, written with invariant numbers and escaped
// text. Characters XML can't hold (control characters, lone surrogates) are left out of the text, so any card text
// gives a well-formed file. A size budget stops a board whose drawing would grow past what the vector engine can load.

using System.Globalization;
using System.Text;
using Filee.Core.Conversion;
using SkiaSharp;

namespace Filee.Engines.Vector.Canvas;

/// <summary>Builds the SVG text of a board.</summary>
internal sealed class CanvasSvg
{
    /// <summary>Largest SVG written (embedded pictures included): Svg.Skia loads it whole.</summary>
    public const int MaxLength = 96 * 1024 * 1024;

    private readonly StringBuilder _svg = new();
    private int _clips;

    /// <summary>Characters written so far.</summary>
    public int Length => _svg.Length;

    public static string N(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    public void Raw(string text)
    {
        _svg.Append(text);
        Check();
    }

    public void Rect(double x, double y, double width, double height, double radius, string? fill, string? stroke = null,
        double strokeWidth = 0, string? extra = null)
    {
        _svg.Append($"""<rect x="{N(x)}" y="{N(y)}" width="{N(Math.Max(0, width))}" height="{N(Math.Max(0, height))}" """);
        if (radius > 0)
            _svg.Append($"""rx="{N(radius)}" ry="{N(radius)}" """);
        _svg.Append($"""fill="{fill ?? "none"}" """);
        if (stroke is not null && strokeWidth > 0)
            _svg.Append($"""stroke="{stroke}" stroke-width="{N(strokeWidth)}" """);
        if (extra is not null)
            _svg.Append(extra).Append(' ');
        _svg.Append("/>");
        Check();
    }

    public void Line(double x1, double y1, double x2, double y2, string stroke, double width)
    {
        _svg.Append($"""<line x1="{N(x1)}" y1="{N(y1)}" x2="{N(x2)}" y2="{N(y2)}" stroke="{stroke}" stroke-width="{N(width)}" stroke-linecap="round"/>""");
        Check();
    }

    public void Path(string data, string? fill, string? stroke = null, double strokeWidth = 0)
    {
        _svg.Append($"""<path d="{data}" fill="{fill ?? "none"}" """);
        if (stroke is not null)
            _svg.Append($"""stroke="{stroke}" stroke-width="{N(strokeWidth)}" stroke-linecap="round" stroke-linejoin="round" """);
        _svg.Append("/>");
        Check();
    }

    /// <summary>
    /// One run of text in one typeface, its baseline starting at (<paramref name="x"/>, <paramref name="y"/>). The
    /// family the run was measured with comes first, so the vector engine draws it in the same typeface.
    /// </summary>
    public void Text(double x, double y, string text, SKTypeface face, double size, bool bold, bool italic, string fill)
    {
        var clean = Clean(text);
        if (clean.Length == 0)
            return;
        _svg.Append($"""<text x="{N(x)}" y="{N(y)}" font-family="{Attribute(FamilyList(face))}" font-size="{N(size)}" """);
        if (bold)
            _svg.Append("""font-weight="bold" """);
        if (italic)
            _svg.Append("""font-style="italic" """);
        _svg.Append($"""fill="{fill}" xml:space="preserve">""");
        Escape(clean);
        _svg.Append("</text>");
        Check();
    }

    /// <summary>How a picture fills its box.</summary>
    public enum Fit
    {
        /// <summary>All of it, its proportions kept, centred (SVG's <c>meet</c>).</summary>
        Contain,

        /// <summary>The whole box, its proportions kept, centred and cut by the clip around it (SVG's <c>slice</c>).</summary>
        Cover,
    }

    /// <summary>The id of each picture embedded so far.</summary>
    private readonly Dictionary<CanvasPicture, string> _pictures = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The id of the picture's one embedded copy (a data URI in <c>&lt;defs&gt;</c>, at the picture's own pixel size),
    /// written the first time it is drawn: a picture shown by many cards, or tiled over a group, is stored once.
    /// </summary>
    private string Define(CanvasPicture picture)
    {
        if (_pictures.TryGetValue(picture, out var id))
            return id;
        if (_svg.Length + (long)picture.Data.Length * 4 / 3 + 256 > MaxLength)
            throw new ConversionRefusedException("error.canvas_too_large", "the pictures of the board are too large together");
        id = $"p{_pictures.Count + 1}";
        _svg.Append($"""<defs><image id="{id}" width="{N(picture.Width)}" height="{N(picture.Height)}" preserveAspectRatio="none" xlink:href="data:{picture.MediaType};base64,""");
        _svg.Append(Convert.ToBase64String(picture.Data));
        _svg.Append("\"/></defs>");
        Check();
        return _pictures[picture] = id;
    }

    /// <summary>The picture fitted into the box, centred.</summary>
    public void Picture(CanvasPicture picture, double x, double y, double width, double height, Fit fit)
    {
        var scaleX = width / picture.Width;
        var scaleY = height / picture.Height;
        var scale = fit == Fit.Contain ? Math.Min(scaleX, scaleY) : Math.Max(scaleX, scaleY);
        var drawnWidth = picture.Width * scale;
        var drawnHeight = picture.Height * scale;
        Place(picture, x + (width - drawnWidth) / 2, y + (height - drawnHeight) / 2, scale, scale);
    }

    /// <summary>The picture at (<paramref name="x"/>, <paramref name="y"/>), <paramref name="width"/> × <paramref name="height"/>, stretched.</summary>
    public void Picture(CanvasPicture picture, double x, double y, double width, double height) =>
        Place(picture, x, y, width / picture.Width, height / picture.Height);

    private void Place(CanvasPicture picture, double x, double y, double scaleX, double scaleY)
    {
        var id = Define(picture);
        _svg.Append($"""<use xlink:href="#{id}" transform="translate({N(x)} {N(y)}) scale({Scale(scaleX)} {Scale(scaleY)})"/>""");
        Check();
    }

    /// <summary>A scale factor with enough digits for a picture shrunk a lot (N keeps two).</summary>
    private static string Scale(double value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Starts a group clipped to a rounded rectangle; close it with <see cref="EndClip"/>.</summary>
    public void BeginClip(double x, double y, double width, double height, double radius)
    {
        var id = $"c{++_clips}";
        _svg.Append($"""<clipPath id="{id}"><rect x="{N(x)}" y="{N(y)}" width="{N(Math.Max(0, width))}" height="{N(Math.Max(0, height))}" rx="{N(radius)}" ry="{N(radius)}"/></clipPath>""");
        _svg.Append($"""<g clip-path="url(#{id})">""");
        Check();
    }

    public void EndClip() => _svg.Append("</g>");

    public override string ToString() => _svg.ToString();

    /// <summary>The family a run was measured with, then generic ones for viewers that don't have it.</summary>
    private static string FamilyList(SKTypeface face)
    {
        var family = face.FamilyName;
        var mono = face.IsFixedPitch;
        var generic = mono ? "monospace" : "sans-serif";
        return string.IsNullOrEmpty(family) ? generic : $"'{family.Replace("'", "", StringComparison.Ordinal)}', {generic}";
    }

    /// <summary>The text with characters XML 1.0 can't hold left out.</summary>
    private static string Clean(string text)
    {
        StringBuilder? clean = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var keep = c is '\t' or '\n' or '\r' || (c >= 0x20 && c < 0xD800) || (c >= 0xE000 && c <= 0xFFFD);
            var width = 1;
            if (!keep && char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                keep = true;
                width = 2;
            }
            if (!keep)
            {
                clean ??= new StringBuilder(text, 0, i, text.Length);
                i += width - 1;
                continue;
            }
            clean?.Append(text, i, width);
            i += width - 1;
        }
        return clean?.ToString() ?? text;
    }

    private void Escape(string text)
    {
        foreach (var c in text)
        {
            _ = c switch
            {
                '<' => _svg.Append("&lt;"),
                '>' => _svg.Append("&gt;"),
                '&' => _svg.Append("&amp;"),
                '\t' or '\n' or '\r' => _svg.Append(' '),
                _ => _svg.Append(c),
            };
        }
    }

    public static string Attribute(string text) =>
        Clean(text).Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal);

    private void Check()
    {
        if (_svg.Length > MaxLength)
            throw new ConversionRefusedException("error.canvas_too_large", "too much to draw");
    }
}
