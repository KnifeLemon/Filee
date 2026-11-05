// Word lets a footnote's number sit anywhere in the note's text, and Arabic papers commonly put it in brackets:
// "(" ref ")". LibreOffice's import drops the number from that place and draws its own label at the start of the note, so
// "(1) text" comes out as "1() text", in a right-to-left paragraph "()1 text" (issue #620). Its label takes no text around
// the number from a DOCX and can't be turned off, but a custom mark replaces it and may be invisible. So for LibreOffice's
// PDF, in a copy of a document with such notes, every reference gets an invisible custom mark followed by its number as
// text, and every note its number as text where Word has it. Text and formatting around them stay as they are.
//
// A custom mark doesn't count in the numbering, so all footnotes change together and the numbers are worked out here.
// That takes numbers that don't depend on the pages: a document restarting them per page or section, or with a number
// format not known here, stays as it was. Endnotes stay LibreOffice's.

using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Office.Ooxml;

namespace Filee.Engines.Office;

/// <summary>Writes the numbers of a Word document's footnotes as text, for LibreOffice.</summary>
internal static class DocxNoteMarks
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>Takes no room: LibreOffice draws it as the note's label, and repeats it after the reference.</summary>
    private const string InvisibleMark = "​";

    /// <summary>The Word formats (DOCX and its template and macro variants).</summary>
    public static bool Reads(string format) => format is "docx" or "docm" or "dotx" or "dotm";

    /// <summary>
    /// A copy of <paramref name="path"/> under the same file name (LibreOffice also goes by the name) with the numbers of
    /// its footnotes as text, or null when no note has text before its number or the numbers can't be worked out here.
    /// </summary>
    public static string? Prepare(string path, string workDirectory)
    {
        try
        {
            string main, notesPath;
            XDocument document, notes;
            using (var package = new OpcPackage(path))
            {
                main = package.MainPartPath ?? "word/document.xml";
                if (package.Part(main) is not { } mainPart
                    || package.TargetOfType(main, "/footnotes") is not { } target || package.Part(target) is not { } notesPart)
                    return null;
                (document, notesPath, notes) = (mainPart, target, notesPart);
                var settings = package.TargetOfType(main, "/settings") is { } settingsPath ? package.Part(settingsPath) : null;
                if (notes.Root?.Elements(W + "footnote").Any(TextBeforeMark) != true || Numbering(document, settings) is not { } numbering)
                    return null;
                WriteNumbers(document, notes, numbering.Start, numbering.Text);
            }

            var folder = Path.Combine(workDirectory, "lo-notes-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, Path.GetFileName(path));
            WriteCopy(path, copy, new(StringComparer.OrdinalIgnoreCase) { [main] = document, [notesPath] = notes });
            return copy;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or XmlException)
        {
            return null; // LibreOffice reports what is wrong with the file
        }
    }

    /// <summary>Whether the note's paragraph has text before the note's number.</summary>
    private static bool TextBeforeMark(XElement note)
    {
        if (note.Descendants(W + "footnoteRef").FirstOrDefault() is not { } mark)
            return false;
        return mark.Ancestors(W + "p").FirstOrDefault()?.Descendants(W + "t")
            .Any(t => t.IsBefore(mark) && !string.IsNullOrWhiteSpace(t.Value)) ?? false;
    }

    private static void WriteNumbers(XDocument document, XDocument notes, int start, Func<int, string> text)
    {
        var numbers = new Dictionary<string, string>();
        var next = start;
        foreach (var reference in References(document).ToList())
        {
            var number = text(next++);
            numbers[(string?)reference.Attribute(W + "id") ?? ""] = number;
            ToCustomMark(reference, number);
        }
        foreach (var note in notes.Root!.Elements(W + "footnote"))
        {
            if ((string?)note.Attribute(W + "id") is { } id && numbers.TryGetValue(id, out var number)
                && note.Descendants(W + "footnoteRef").FirstOrDefault() is { } mark)
                mark.ReplaceWith(Text(number));
        }
    }

    /// <summary>The references that show a number, in the order of the document: no custom marks, deleted text or fallbacks.</summary>
    private static IEnumerable<XElement> References(XDocument document) =>
        document.Descendants(W + "footnoteReference").Where(r =>
            (string?)r.Attribute(W + "customMarkFollows") is not ("1" or "true" or "on")
            && !r.Ancestors().Any(a => a.Name == W + "del" || a.Name == W + "moveFrom" || a.Name == Mc + "Fallback"));

    /// <summary>The reference with its invisible mark, then the number in a run of the same formatting.</summary>
    private static void ToCustomMark(XElement reference, string number)
    {
        reference.SetAttributeValue(W + "customMarkFollows", "1");
        var run = reference.Parent!;
        var numberRun = new XElement(W + "r", run.Element(W + "rPr") is { } format ? new XElement(format) : null, Text(number));
        var rest = reference.ElementsAfterSelf().ToList();
        rest.ForEach(e => e.Remove());
        numberRun.Add(rest);
        reference.AddAfterSelf(Text(InvisibleMark));
        run.AddAfterSelf(numberRun);
    }

    private static XElement Text(string text) => new(W + "t", text);

    /// <summary>How footnotes are numbered, or null when that can't be worked out without the pages or the format is unknown.</summary>
    private static (int Start, Func<int, string> Text)? Numbering(XDocument document, XDocument? settings)
    {
        var properties = W + "footnotePr";
        var all = (settings?.Root?.Elements(properties) ?? []).Concat(document.Descendants(W + "sectPr").Elements(properties)).ToList();
        if (all.Elements(W + "numRestart").Any(r => (string?)r.Attribute(W + "val") is not ("continuous" or null)))
            return null;
        var formats = all.Elements(W + "numFmt").Select(f => (string?)f.Attribute(W + "val")).Distinct().ToList();
        var starts = all.Elements(W + "numStart").Select(s => (string?)s.Attribute(W + "val")).Distinct().ToList();
        if (formats.Count > 1 || starts.Count > 1)
            return null;
        var start = starts.Count == 1 && int.TryParse(starts[0], CultureInfo.InvariantCulture, out var first) ? first : 1;
        return formats.FirstOrDefault() switch
        {
            null or "decimal" => (start, n => n.ToString(CultureInfo.InvariantCulture)),
            "hindiNumbers" => (start, n => string.Concat(n.ToString(CultureInfo.InvariantCulture).Select(d => (char)('٠' + (d - '0'))))),
            _ => null,
        };
    }

    /// <summary>A copy of the package with <paramref name="changed"/> parts in place of the originals.</summary>
    private static void WriteCopy(string path, string copy, Dictionary<string, XDocument> changed)
    {
        using var source = ZipFile.OpenRead(path);
        using var file = File.Create(copy);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var entry in source.Entries)
        {
            using var output = zip.CreateEntry(entry.FullName).Open();
            if (changed.TryGetValue(entry.FullName, out var part))
            {
                part.Save(output, SaveOptions.DisableFormatting);
                continue;
            }
            using var input = entry.Open();
            input.CopyTo(output);
        }
    }
}
