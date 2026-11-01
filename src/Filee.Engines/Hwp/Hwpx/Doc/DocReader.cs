// Reads Word 97–2003 documents (.doc, .dot, and WPS Office's .wps, the same format) without Word or LibreOffice: the
// compound file's streams are written out as the DOCX package Word itself would save (DocToDocx) and read by
// DocxReader, so both Word formats share one reader and every mapping of it.
//
// A .doc that is really another format (Word saves RTF and web pages with a .doc name when asked to, and some
// programs name DOCX files .doc) is read by that format's reader. Password-protected documents, Word 6.0 / 95 files and
// Microsoft Works documents are refused by DocFile with a message; Unsupported names the last two (and Works) for the
// route planner, which then takes LibreOffice when it is installed.

using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx.Docx;

namespace Filee.Engines.Hwp.Hwpx.Doc;

/// <summary>Reads .doc files into the document model.</summary>
internal static class DocReader
{
    /// <summary>The formats of Word 97–2003 binary files: documents, templates and WPS Office's documents.</summary>
    public static readonly string[] Formats = ["doc", "dot", "wps"];

    /// <summary>Reads <paramref name="path"/>; embedded pictures are extracted into <paramref name="mediaFolder"/>.</summary>
    public static HDocument Read(string path, string mediaFolder) => Read(path, mediaFolder, DocumentLimits.Default);

    /// <summary>Reads within other <paramref name="limits"/> than <see cref="DocumentLimits.Default"/> (for tests).</summary>
    internal static HDocument Read(string path, string mediaFolder, DocumentLimits limits)
    {
        switch (Sniff(path))
        {
            case Kind.Zip:
                return DocxReader.Read(path, mediaFolder, limits);
            case Kind.Rtf:
                return RtfReader.Read(path, mediaFolder);
            case Kind.Html:
                return HtmlReader.Read(path, mediaFolder);
        }

        // The package goes next to the media folder, in the job's scratch folder.
        var folder = Path.GetDirectoryName(Path.GetFullPath(mediaFolder)) ?? Path.GetTempPath();
        Directory.CreateDirectory(folder);
        var docx = Path.Combine(folder, $"doc-{Guid.NewGuid():N}.docx");
        try
        {
            ToDocx(path, docx, limits);
            return DocxReader.Read(docx, mediaFolder, limits);
        }
        finally
        {
            File.Delete(docx);
        }
    }

    /// <summary>Writes the WordprocessingML package of the DOC document at <paramref name="path"/> to <paramref name="output"/>.</summary>
    public static void ToDocx(string path, string output, DocumentLimits? limits = null)
    {
        if (Sniff(path) == Kind.Zip)
        {
            File.Copy(path, output, overwrite: true); // already a Word package
            return;
        }
        using var file = DocFile.Open(path, limits ?? DocumentLimits.Default);
        DocToDocx.Write(file, output);
    }

    /// <summary>
    /// What Filee doesn't read in <paramref name="path"/> ("a Word 6.0 / 95 document"), or null when it reads the file
    /// (or the file is damaged, which the reader then says).
    /// </summary>
    public static string? Unsupported(string path) => DocFile.Unsupported(path);

    private enum Kind
    {
        Compound,
        Zip,
        Rtf,
        Html,
    }

    /// <summary>What the file starts with: a ZIP package, RTF, an HTML page, or (anything else) a compound file.</summary>
    private static Kind Sniff(string path)
    {
        var start = new byte[512];
        int read;
        using (var file = File.OpenRead(path))
            read = file.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        var bytes = start.AsSpan(0, read);
        if (bytes is [0xD0, 0xCF, 0x11, 0xE0, ..])
            return Kind.Compound;
        if (bytes is [0x50, 0x4B, 0x03, 0x04, ..])
            return Kind.Zip;
        var text = System.Text.Encoding.Latin1.GetString(bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes).TrimStart();
        if (text.StartsWith(@"{\rtf", StringComparison.Ordinal))
            return Kind.Rtf;
        if (text.StartsWith("<", StringComparison.Ordinal) && (text.Contains("<html", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)))
            return Kind.Html;
        return Kind.Compound;
    }
}
