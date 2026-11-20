// What the slide renderer doesn't draw yet, named before a deck is converted (ICapabilityCheck through
// FileeRendererConverter): such a deck takes the route it would take without the renderer (LibreOffice). Only the
// shown slides and the layouts and masters they use are looked at, as the reader reads them.
//
// Named: charts ChartReader doesn't draw (3D, radar, stock, ..., and chartex) without a stored picture, SmartArt without its stored drawing, OLE objects and other embedded content
// without a picture, ink, shadows and other effects (glow, reflection, soft edges, blur; on shapes, pictures and text,
// or through the theme's effect styles), 3D, WordArt warps, outlined text, preset shapes not
// in PresetShapes, and pictures Skia can't decode (TIFF, JPEG XR).
//
// A PowerPoint 97–2003 deck is looked through as the PPTX package it translates to, after what the translation leaves
// out (Office/Ppt: freeforms, WordArt, ...); a PowerPoint 95 file is named as such.
//
// Each slide, layout and master is looked through once, in one pass over its elements, however often the presentation
// lists it or slides use it; the scan stops when the conversion is cancelled.
//
// The same pass, run to the end (Count), is the deck's note when the renderer draws it anyway (no LibreOffice, or the
// user's choice): each kind it met with its count (SlideLeftOut), and objects and charts drawn from their stored
// pictures, which the check lets through.

using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Office.Ppt;

namespace Filee.Engines.Slides;

internal static class SlideCapabilities
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = DrawingColors.A;
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly HashSet<string> Effects = ["outerShdw", "innerShdw", "prstShdw", "glow", "reflection", "softEdge", "blur"];

    /// <summary>The first thing in the deck the renderer doesn't draw; null when it draws everything (or the file can't be read).</summary>
    public static string? FirstUnsupported(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            if (PptReader.Unsupported(path) is { } old)
                return old;
            using var package = SlidePackage.Open(path);
            return package.Dropped ?? Scan(package, null, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or Core.Conversion.ConversionRefusedException)
        {
            // A damaged or hostile file: the conversion stops with the reader's own message.
            return null;
        }
    }

    /// <summary>
    /// What the renderer leaves out of or draws another way in the deck in <paramref name="package"/>, counted by kind
    /// for the deck's note: what a PowerPoint 97–2003 translation left out, everything the check would name, and
    /// embedded objects and charts drawn from the picture stored for them. The check's pass, to the end.
    /// </summary>
    public static void Count(SlidePackage package, SlideLeftOut leftOut, CancellationToken cancellationToken = default)
    {
        foreach (var (kind, count) in package.DroppedKinds)
            leftOut.Add(kind, count);
        Scan(package, leftOut, cancellationToken);
    }

    /// <summary>The first feature named; with <paramref name="leftOut"/>, every element is looked at and counted.</summary>
    private static string? Scan(SlidePackage package, SlideLeftOut? leftOut, CancellationToken cancellationToken)
    {
        var presentationPart = package.MainPart ?? "ppt/presentation.xml";
        var presentation = package.Part(presentationPart);
        if (presentation is null)
            return null;
        // Slides, then their layouts and masters, each once however often it is listed or used.
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var themes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in presentation.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slide = package.Target(presentationPart, (string?)id.Attribute(R + "id"));
            if (slide is null || !listed.Add(slide) || package.Part(slide) is not { } root || (string?)root.Attribute("show") is "0" or "false")
                continue;
            if (seen.Add(slide))
                parts.Add(slide);
            var layout = package.TargetOfType(slide, "/slideLayout");
            var master = layout is null ? null : package.TargetOfType(layout, "/slideMaster");
            foreach (var part in new[] { layout, master })
            {
                if (part is not null && seen.Add(part))
                    parts.Add(part);
            }
            if (master is not null && package.TargetOfType(master, "/theme") is { } theme)
                themes.Add(theme);
        }

        // Effects the theme's styles give shapes that reference them (effectRef idx = 1, 2, 3).
        var themedEffects = new HashSet<int>();
        foreach (var theme in themes)
        {
            var styles = package.Part(theme)?.Descendants(A + "effectStyleLst").FirstOrDefault()?.Elements(A + "effectStyle").ToList() ?? [];
            for (var i = 0; i < styles.Count; i++)
            {
                if (styles[i].Element(A + "effectLst")?.Elements().Any(e => Effects.Contains(e.Name.LocalName)) == true)
                    themedEffects.Add(i + 1);
            }
        }

        string? first = null;
        foreach (var part in parts)
        {
            if (package.Part(part) is not { } root)
                continue;
            var feature = Part(package, part, root, themedEffects, leftOut, cancellationToken);
            first ??= feature;
            if (first is not null && leftOut is null)
                return first;
        }
        return first;
    }

    /// <summary>
    /// The elements under <paramref name="root"/> in document order, without what a fallback replaces: the choice of an
    /// mc:AlternateContent that has a fallback is not drawn, so it isn't looked at.
    /// </summary>
    private static IEnumerable<XElement> Drawn(XElement root)
    {
        var open = new Stack<IEnumerator<XElement>>();
        open.Push(root.Elements().GetEnumerator());
        while (open.Count > 0)
        {
            var children = open.Peek();
            if (!children.MoveNext())
            {
                children.Dispose();
                open.Pop();
                continue;
            }
            var element = children.Current;
            yield return element;
            var below = element.Elements();
            if (element.Name.LocalName == "AlternateContent" && below.Any(e => e.Name.LocalName == "Fallback"))
                below = below.Where(e => e.Name.LocalName != "Choice");
            open.Push(below.GetEnumerator());
        }
    }

    /// <summary>
    /// What an element is to the check (<c>Feature</c>, null when it is drawn as it is) and to the note (<c>Kind</c>,
    /// null when nothing is lost).
    /// </summary>
    private readonly record struct Finding(string? Feature, string? Kind);

    private static string? Part(SlidePackage package, string part, XElement root, HashSet<int> themedEffects, SlideLeftOut? leftOut,
        CancellationToken cancellationToken)
    {
        var count = 0;
        string? first = null;
        foreach (var element in Drawn(root))
        {
            if (++count % 4096 == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var finding = element.Name.LocalName switch
            {
                "graphicData" => Graphic(package, part, element),
                "contentPart" => new Finding("ink", "ink"),
                "effectLst" when element.Elements().Any(e => Effects.Contains(e.Name.LocalName)) => new Finding("shadows and effects", "effects"),
                "effectDag" => new Finding("shadows and effects", "effects"),
                "effectRef" when int.TryParse((string?)element.Attribute("idx"), out var index) && themedEffects.Contains(index) =>
                    new Finding("shadows and effects", "effects"),
                "sp3d" when element.HasElements || element.Attributes().Any(a => a.Name.LocalName is "extrusionH" or "contourW" or "z") =>
                    new Finding("3D effects", "effects"),
                "prstTxWarp" when (string?)element.Attribute("prst") is { } warp && warp != "textNoShape" => new Finding("WordArt text warp", "wordart"),
                "ln" when element.Parent?.Name.LocalName is "rPr" or "defRPr" or "endParaRPr" && element.Elements().Any(e => e.Name.LocalName != "noFill") =>
                    new Finding("outlined text", "effects"),
                "prstGeom" when (string?)element.Attribute("prst") is { } preset && !PresetShapes.Known(preset) =>
                    new Finding($"the preset shape {preset}", "shapes"),
                "blip" => Picture(package, part, element),
                _ => default,
            };
            if (finding.Kind is { } kind)
                leftOut?.Add(kind);
            first ??= finding.Feature;
            if (first is not null && leftOut is null)
                return first;
        }
        return first;
    }

    /// <summary>
    /// A graphic frame's content: tables are drawn; charts, SmartArt and objects only with what they store for drawing.
    /// An object, or a chart ChartReader doesn't draw, is drawn from its stored picture when it has one (the note says so).
    /// </summary>
    private static Finding Graphic(SlidePackage package, string part, XElement data)
    {
        var uri = (string?)data.Attribute("uri") ?? "";
        if (data.Element(A + "tbl") is not null)
            return default;
        var pictured = data.Descendants(P + "pic").Any();
        if (uri.EndsWith("/chart", StringComparison.Ordinal))
        {
            var target = package.Target(part, (string?)data.Element(Charts.ChartReader.C + "chart")?.Attribute(R + "id"));
            if (Charts.ChartReader.Read(target is null ? null : package.Part(target), DrawingColors.FromTheme(null)).LeftOut is not { } chart)
                return default;
            return pictured ? new Finding(null, "charts_pictured") : new Finding(chart, "charts");
        }
        if (uri.EndsWith("/chartex", StringComparison.Ordinal))
            return pictured ? new Finding(null, "charts_pictured") : new Finding("charts", "charts");
        // SmartArt is drawn from the drawing PowerPoint keeps with it, never from a picture.
        if (uri.EndsWith("/diagram", StringComparison.Ordinal))
            return package.Part(package.DiagramDrawing(part, data)) is not null ? default : new Finding(pictured ? null : "SmartArt", "smartart");
        if (pictured)
            return new Finding(null, "objects_pictured");
        return new Finding(uri.EndsWith("/ole", StringComparison.Ordinal) ? "OLE objects" : "embedded objects", "objects");
    }

    private static Finding Picture(SlidePackage package, string part, XElement blip)
    {
        var target = package.Target(part, (string?)blip.Attribute(R + "embed"));
        var extension = target is null ? "" : DocumentPackage.SafeExtension(target);
        return extension switch
        {
            ".tif" or ".tiff" => new Finding("TIFF pictures", "pictures"),
            ".wdp" or ".jxr" or ".hdp" => new Finding("JPEG XR pictures", "pictures"),
            _ => default,
        };
    }
}
