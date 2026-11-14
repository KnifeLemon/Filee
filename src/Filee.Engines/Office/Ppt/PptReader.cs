// Reads PowerPoint 97–2003 presentations (.ppt, .pps, .pot: format id "ppt") without PowerPoint or LibreOffice: the
// compound file's records are written out as the PPTX package PowerPoint would save (PptToPptx), which the PPTX
// readers take from there: the slide renderer (Slides/SlidePackage opens a .ppt this way), PptxReader for the document
// writers (DOCX, HWPX, ODT, text), and PPTX output as it is.
//
// A .ppt that is really a PPTX (some programs name them so) is read as one. Password-protected presentations, and
// PowerPoint 95 and older files, are refused by PptFile with a message; Unsupported names the old ones for the route
// planner, which then takes LibreOffice when it is installed.

using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Pptx;

namespace Filee.Engines.Office.Ppt;

internal static class PptReader
{
    /// <summary>The format id of PowerPoint 97–2003 presentations, templates and slide shows.</summary>
    public const string Format = "ppt";

    /// <summary>Reads <paramref name="path"/> into the document model; pictures are extracted into <paramref name="mediaFolder"/>.</summary>
    public static HDocument Read(string path, string mediaFolder)
    {
        if (!CompoundFile.HasSignature(path))
            return PptxReader.Read(path, mediaFolder);
        // The package goes next to the media folder, in the job's scratch folder.
        var folder = Path.GetDirectoryName(Path.GetFullPath(mediaFolder)) ?? Path.GetTempPath();
        Directory.CreateDirectory(folder);
        var pptx = Path.Combine(folder, $"ppt-{Guid.NewGuid():N}.pptx");
        try
        {
            ToPptx(path, pptx);
            return PptxReader.Read(pptx, mediaFolder);
        }
        finally
        {
            File.Delete(pptx);
        }
    }

    /// <summary>Writes the PPTX package of the presentation at <paramref name="path"/> to <paramref name="output"/>.</summary>
    public static void ToPptx(string path, string output, DocumentLimits? limits = null, CancellationToken cancellationToken = default)
    {
        if (!CompoundFile.HasSignature(path))
        {
            File.Copy(path, output, overwrite: true); // already a package
            return;
        }
        using var file = PptFile.Open(path, limits ?? DocumentLimits.Default);
        var temp = output + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = File.Create(temp))
                PptToPptx.Write(file, stream, cancellationToken);
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>
    /// The PPTX package of the presentation at <paramref name="path"/> in memory, with what the translation left out that
    /// would show (<paramref name="dropped"/>).
    /// </summary>
    public static MemoryStream ToPptx(string path, DocumentLimits limits, out string? dropped, CancellationToken cancellationToken = default)
    {
        using var file = PptFile.Open(path, limits);
        var stream = new MemoryStream();
        dropped = PptToPptx.Write(file, stream, cancellationToken);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// What Filee doesn't read in <paramref name="path"/> ("a PowerPoint 95 or older presentation"), or null when it
    /// reads the file (or the file is damaged, which the reader then says).
    /// </summary>
    public static string? Unsupported(string path) => PptFile.Unsupported(path);
}
