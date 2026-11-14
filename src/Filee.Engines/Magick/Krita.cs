// Krita's own file (.kra, issue #903) is a ZIP of the layers, the document's XML and, at its top, mergedimage.png: the
// whole picture as Krita shows it, written for other programs (the OpenRaster convention). Filee reads only that picture,
// in memory; the entry's name is never used as a path.

using System.IO.Compression;

namespace Filee.Engines.Magick;

internal static class Krita
{
    /// <summary>The merged picture of a large canvas is a big PNG; much more than this is not one.</summary>
    private const long MaxImageBytes = 256L * 1024 * 1024;

    private const string Merged = "mergedimage.png";

    /// <summary>The PNG bytes of the picture as Krita shows it.</summary>
    /// <exception cref="InvalidDataException">The file has no merged picture, or it is empty or too large.</exception>
    public static byte[] ReadMerged(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(Merged)
                    ?? throw new InvalidDataException(
                        "This Krita file has no merged picture (mergedimage.png). Open it in Krita and save it again, or " +
                        "export it as PNG.");
        if (entry.Length <= 0 || entry.Length > MaxImageBytes)
            throw new InvalidDataException("The Krita file's merged picture is empty or too large.");
        var data = new byte[entry.Length];
        using (var stream = entry.Open())
            stream.ReadExactly(data);
        return data;
    }
}
