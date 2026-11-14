// The speaker notes of a PowerPoint deck as text, for the ODP writer's notes pages: per shown slide (in the order and
// with the rule PptxSlideReader reads slides: listed once, hidden ones left out), the paragraphs of its notes slide's
// body placeholder, a line break starting a new paragraph. The slide model itself has no notes.
//
// Through SlidePackage, so a PowerPoint 97–2003 file is read as the PPTX package it translates to and the package's
// limits hold; the text is capped at the part limit in characters.

using System.Text;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Slides;

namespace Filee.Engines.Odf.Draw;

internal static class PptxNotes
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Paragraphs of notes per shown slide (an empty list for a slide without notes).</summary>
    public static List<IReadOnlyList<string>> Read(string path, DocumentLimits? limits = null, CancellationToken cancellationToken = default)
    {
        using var package = SlidePackage.Open(path, limits);
        var result = new List<IReadOnlyList<string>>();
        var presentationPart = package.MainPart ?? "ppt/presentation.xml";
        if (package.Part(presentationPart) is not { } presentation)
            return result;
        long characters = 0;
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in presentation.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slidePart = package.Target(presentationPart, (string?)id.Attribute(R + "id"));
            if (slidePart is null || !listed.Add(slidePart) || package.Part(slidePart) is not { } slide || slide.Name != P + "sld")
                continue;
            if ((string?)slide.Attribute("show") is "0" or "false" or "off")
                continue;
            var lines = new List<string>();
            if (package.Part(package.TargetOfType(slidePart, "/notesSlide")) is { } notes)
            {
                foreach (var shape in notes.Descendants(P + "sp"))
                {
                    var placeholder = shape.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph");
                    if ((string?)placeholder?.Attribute("type") != "body")
                        continue;
                    foreach (var paragraph in shape.Element(P + "txBody")?.Elements(A + "p") ?? [])
                    {
                        var line = new StringBuilder();
                        foreach (var run in paragraph.Elements())
                        {
                            if (run.Name == A + "br")
                            {
                                lines.Add(line.ToString());
                                line.Clear();
                            }
                            else if (run.Name == A + "r" || run.Name == A + "fld")
                            {
                                line.Append(run.Element(A + "t")?.Value);
                            }
                        }
                        lines.Add(line.ToString());
                        characters += line.Length;
                        if (characters > package.Limits.MaxPartBytes)
                            throw DocumentLimits.Exceeded($"The presentation's notes are longer than {DocumentLimits.Count(package.Limits.MaxPartBytes)} characters.");
                    }
                }
            }
            // Empty lines at the end are PowerPoint's empty paragraph after the text.
            while (lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
            result.Add(lines);
        }
        return result;
    }
}
