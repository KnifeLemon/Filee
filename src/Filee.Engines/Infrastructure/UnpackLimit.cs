// What one document's reader unpacks to files next to it, its pictures mostly. A few kilobytes can unpack to terabytes,
// so it is counted while it is written (a lying header doesn't help) and the document is refused past the limit.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Engines.Office;

namespace Filee.Engines.Infrastructure;

/// <summary>Unpacks a document's entries to files, up to <paramref name="maxBytes"/> in all.</summary>
internal sealed class UnpackLimit(long maxBytes)
{
    private long _left = maxBytes;

    /// <summary>The limit of the package at <paramref name="path"/>: the one its copies have (<see cref="PackageRepacker"/>).</summary>
    public static UnpackLimit For(string path) => new(PackageRepacker.MaxUnpacked(path));

    /// <summary>Writes <paramref name="entry"/> to <paramref name="path"/>.</summary>
    /// <exception cref="ConversionRefusedException">The document has unpacked to more than the limit.</exception>
    public void Extract(ZipArchiveEntry entry, string path)
    {
        if (entry.Length > _left)
            throw TooLarge(entry);
        using var source = entry.Open();
        using var output = File.Create(path);
        var buffer = new byte[81920];
        int count;
        while ((count = source.Read(buffer)) > 0)
        {
            _left -= count;
            if (_left < 0)
                throw TooLarge(entry);
            output.Write(buffer, 0, count);
        }
    }

    private ConversionRefusedException TooLarge(ZipArchiveEntry entry) =>
        new("error.document_too_large", $"{entry.FullName}: unpacks to over {BoundedXml.Size(maxBytes)}");
}
