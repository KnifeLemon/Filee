// What one document's reader unpacks to files next to it, its pictures mostly, and what a converter copies of a package
// into a new one (a KEPUB, a cleaned-up Office file, a workbook prepared for printing). A few kilobytes can unpack to
// terabytes, so it is counted while it is written (a lying header doesn't help) and the document is refused past the
// limit.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Engines.Office;

namespace Filee.Engines.Infrastructure;

/// <summary>Unpacks a document's entries to files, up to <paramref name="maxBytes"/> in all.</summary>
internal sealed class UnpackLimit(long maxBytes)
{
    private readonly long _max = maxBytes;
    private long _left = maxBytes;

    /// <summary>The limit of the package at <paramref name="path"/>: the one its copies have (<see cref="PackageRepacker"/>).</summary>
    public static UnpackLimit For(string path) => new(PackageRepacker.MaxUnpacked(path));

    /// <summary>Writes <paramref name="entry"/> to <paramref name="path"/>.</summary>
    /// <exception cref="ConversionRefusedException">The document has unpacked to more than the limit.</exception>
    public void Extract(ZipArchiveEntry entry, string path)
    {
        using var output = File.Create(path);
        Copy(entry, output, long.MaxValue);
    }

    /// <summary>Copies <paramref name="entry"/> into <paramref name="output"/> (an entry of the package being written).</summary>
    /// <exception cref="ConversionRefusedException">The document has unpacked to more than the limit.</exception>
    public void Copy(ZipArchiveEntry entry, Stream output) => Copy(entry, output, long.MaxValue);

    /// <summary>The content of <paramref name="entry"/>, which may be no larger than <paramref name="maxBytes"/>.</summary>
    /// <exception cref="ConversionRefusedException">The entry or the document has unpacked to more than its limit.</exception>
    public byte[] Read(ZipArchiveEntry entry, long maxBytes)
    {
        using var memory = new MemoryStream();
        Copy(entry, memory, maxBytes);
        return memory.ToArray();
    }

    private void Copy(ZipArchiveEntry entry, Stream output, long maxBytes)
    {
        if (entry.Length > _left)
            throw TooLarge(entry, _max);
        if (entry.Length > maxBytes)
            throw TooLarge(entry, maxBytes);
        using var source = entry.Open();
        var buffer = new byte[81920];
        long written = 0;
        int count;
        while ((count = source.Read(buffer)) > 0)
        {
            _left -= count;
            written += count;
            if (_left < 0)
                throw TooLarge(entry, _max);
            if (written > maxBytes)
                throw TooLarge(entry, maxBytes);
            output.Write(buffer, 0, count);
        }
    }

    private static ConversionRefusedException TooLarge(ZipArchiveEntry entry, long limit) =>
        new("error.document_too_large", $"{entry.FullName}: unpacks to over {BoundedXml.Size(limit)}");
}
