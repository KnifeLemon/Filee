// Small PDFs written object by object, for what PDFsharp's own writer would add or refuse: no document information, an
// XMP packet of one's own, a page tree that contains itself.

using System.Text;

namespace Filee.Engines.Tests;

/// <summary>Writes a PDF from object bodies as given, with a matching cross-reference table.</summary>
internal static class RawPdf
{
    /// <summary>
    /// Writes objects 1, 2, 3 … from <paramref name="objects"/>; object 1 is the catalog. <paramref name="trailer"/> is
    /// added to the trailer dictionary (e.g. <c>/Info 9 0 R</c>).
    /// </summary>
    public static string Write(string path, IReadOnlyList<string> objects, string trailer = "")
    {
        var pdf = new StringBuilder("%PDF-1.7\n%\u00e2\u00e3\u00cf\u00d3\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R ").Append(trailer).Append(" >>\n")
            .Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(pdf.ToString()));
        return path;
    }

    /// <summary>A stream object: <paramref name="dictionary"/> entries, its length and the data (Latin-1, unfiltered).</summary>
    public static string Stream(string dictionary, string data) =>
        $"<< {dictionary} /Length {Encoding.Latin1.GetByteCount(data)} >>\nstream\n{data}\nendstream";
}
