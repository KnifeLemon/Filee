// Whether an Office document carries macros, so the result can say they were not run (LibreOffice's worker profiles
// never run them, see LibreOfficeProfile). Looks only at where the formats keep them:
//  * Office Open XML (.docm, .xlsm, .pptm …): a vbaProject.bin part;
//  * OpenDocument: Basic modules (Basic/<library>/<module>.xml) or scripts (Scripts/…);
//  * Word 97 and Excel 97 files: a VBA project storage, whose directory entry names _VBA_PROJECT in UTF-16.
// PowerPoint 97 keeps its VBA compressed inside the document stream and is not detected.

using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Office;

internal static class OfficeMacros
{
    private static readonly byte[] CompoundFile = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] VbaProject = Encoding.Unicode.GetBytes("_VBA_PROJECT");

    /// <summary>True when <paramref name="path"/> has macros; false also when it can't be read.</summary>
    public static bool In(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
                return false;
            stream.Position = 0;
            if (head[0] == 'P' && head[1] == 'K')
                return InPackage(stream);
            return head.SequenceEqual(CompoundFile) && Contains(stream, VbaProject);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool InPackage(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return zip.Entries.Any(entry =>
            entry.Name.Equals("vbaProject.bin", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.StartsWith("Scripts/", StringComparison.Ordinal) && entry.Length > 0
            || entry.FullName.StartsWith("Basic/", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal)
               && entry.Name is not ("script-lc.xml" or "script-lb.xml" or "dialog-lc.xml" or "dialog-lb.xml"));
    }

    /// <summary>Searches the whole stream for <paramref name="pattern"/>, in pieces that overlap by its length.</summary>
    private static bool Contains(Stream stream, byte[] pattern)
    {
        var buffer = new byte[1 << 20];
        var kept = 0;
        int read;
        while ((read = stream.Read(buffer, kept, buffer.Length - kept)) > 0)
        {
            var filled = kept + read;
            if (buffer.AsSpan(0, filled).IndexOf(pattern) >= 0)
                return true;
            kept = Math.Min(pattern.Length - 1, filled);
            buffer.AsSpan(filled - kept, kept).CopyTo(buffer);
        }
        return false;
    }
}
