// What the slide renderer doesn't draw yet, named before a deck is converted (ICapabilityCheck through
// FileeRendererConverter): such a deck takes the route it would take without the renderer (LibreOffice). Only the
// shown slides and the layouts and masters they use are looked at, as the reader reads them.
//
// Named: charts without a stored picture, SmartArt without its stored drawing, OLE objects and other embedded content
// without a picture, ink, shadows and other effects (glow, reflection, soft edges, blur; on shapes, pictures and text,
// or through the theme's effect styles), 3D, WordArt warps, outlined text, East Asian vertical text, preset shapes not
// in PresetShapes, and pictures Skia can't decode (TIFF, JPEG XR).

using System.Xml.Linq;
using Filee.Engines.Documents;

namespace Filee.Engines.Slides;

internal static class SlideCapabilities
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = DrawingColors.A;
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly HashSet<string> Effects = ["outerShdw", "innerShdw", "prstShdw", "glow", "reflection", "softEdge", "blur"];

    /// <summary>The first thing in the deck the renderer doesn't draw; null when it draws everything (or the file can't be read).</summary>
    public static string? FirstUnsupported(string path)
    {
        try
        {
            using var package = SlidePackage.Open(path);
            return Scan(package);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or Core.Conversion.ConversionRefusedException)
        {
            // A damaged or hostile file: the conversion stops with the reader's own message.
            return null;
        }
    }

    private static string? Scan(SlidePackage package)
    {
        var presentationPart = package.MainPart ?? "ppt/presentation.xml";
        var presentation = package.Part(presentationPart);
        if (presentation is null)
            return null;
        var parts = new List<string>();
        var themes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in presentation.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        {
            var slide = package.Target(presentationPart, (string?)id.Attribute(R + "id"));
            if (package.Part(slide) is not { } root || (string?)root.Attribute("show") is "0" or "false")
                continue;
            parts.Add(slide!);
            var layout = package.TargetOfType(slide!, "/slideLayout");
            var master = layout is null ? null : package.TargetOfType(layout, "/slideMaster");
            foreach (var part in new[] { layout, master })
            {
                if (part is not null && !parts.Contains(part))
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

        foreach (var part in parts)
        {
            if (package.Part(part) is not { } root || Part(package, part, root, themedEffects) is not { } feature)
                continue;
            return feature;
        }
        return null;
    }

    private static string? Part(SlidePackage package, string part, XElement root, HashSet<int> themedEffects)
    {
        foreach (var element in root.Descendants())
        {
            // What a fallback (mc:AlternateContent) replaces is not drawn, so it isn't looked at.
            if (element.Ancestors().Any(a => a.Name.LocalName == "Choice") && element.Ancestors().Any(a => a.Name.LocalName == "AlternateContent"
                    && a.Elements().Any(e => e.Name.LocalName == "Fallback")))
                continue;
            var feature = element.Name.LocalName switch
            {
                "graphicData" => Graphic(package, part, element),
                "contentPart" => "ink",
                "effectLst" when element.Elements().Any(e => Effects.Contains(e.Name.LocalName)) => "shadows and effects",
                "effectDag" => "shadows and effects",
                "effectRef" when int.TryParse((string?)element.Attribute("idx"), out var index) && themedEffects.Contains(index) => "shadows and effects",
                "sp3d" when element.HasElements || element.Attributes().Any(a => a.Name.LocalName is "extrusionH" or "contourW" or "z") => "3D effects",
                "prstTxWarp" when (string?)element.Attribute("prst") is { } warp && warp != "textNoShape" => "WordArt text warp",
                "ln" when element.Parent?.Name.LocalName is "rPr" or "defRPr" or "endParaRPr" && element.Elements().Any(e => e.Name.LocalName != "noFill") =>
                    "outlined text",
                "bodyPr" when (string?)element.Attribute("vert") is "eaVert" or "wordArtVert" or "mongolianVert" or "wordArtVertRtl" => "vertical East Asian text",
                "prstGeom" when (string?)element.Attribute("prst") is { } preset && !PresetShapes.Known(preset) => $"the preset shape {preset}",
                "blip" => Picture(package, part, element),
                _ => null,
            };
            if (feature is not null)
                return feature;
        }
        return null;
    }

    /// <summary>A graphic frame's content: tables are drawn; charts, SmartArt and objects only with what they store for drawing.</summary>
    private static string? Graphic(SlidePackage package, string part, XElement data)
    {
        var uri = (string?)data.Attribute("uri") ?? "";
        if (data.Element(A + "tbl") is not null || data.Descendants(P + "pic").Any())
            return null;
        if (uri.EndsWith("/chart", StringComparison.Ordinal) || uri.EndsWith("/chartex", StringComparison.Ordinal))
            return "charts";
        if (uri.EndsWith("/diagram", StringComparison.Ordinal))
        {
            var dataPart = package.Target(part, (string?)data.Descendants().FirstOrDefault(e => e.Name.LocalName == "relIds")?.Attribute(R + "dm"));
            var drawingId = (string?)package.Part(dataPart)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "dataModelExt")?.Attribute("relId");
            return package.Part(package.Target(part, drawingId)) is null ? "SmartArt" : null;
        }
        if (uri.EndsWith("/ole", StringComparison.Ordinal))
            return "OLE objects";
        return "embedded objects";
    }

    private static string? Picture(SlidePackage package, string part, XElement blip)
    {
        var target = package.Target(part, (string?)blip.Attribute(R + "embed"));
        var extension = target is null ? "" : DocumentPackage.SafeExtension(target);
        return extension switch
        {
            ".tif" or ".tiff" => "TIFF pictures",
            ".wdp" or ".jxr" or ".hdp" => "JPEG XR pictures",
            _ => null,
        };
    }
}
