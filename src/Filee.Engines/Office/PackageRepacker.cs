// Rewrites the ZIP container of an Office Open XML or OpenDocument file with plain structure, for LibreOffice.
// LibreOffice's package reader (26.2) refuses archives that other readers open, such as Tencent Docs exports (issue
// #141): it fails on entries stored uncompressed with a data descriptor, bytes between entries and a name that appears
// twice, saying only "source file could not be loaded". The parts are fine, so a copy with the same parts written by
// System.IO.Compression loads. The copies are checked while they are made: a few kilobytes can unpack to terabytes.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Office;

/// <summary>Copies an Office Open XML or OpenDocument package into a new, plainly structured ZIP.</summary>
internal static class PackageRepacker
{
    private const string ContentTypes = "[Content_Types].xml";
    private const string Mimetype = "mimetype";

    /// <summary>
    /// What a copy may unpack to at least. A larger file may unpack to twice its own size: a presentation of many
    /// gigabytes of video is stored as it is, and its size doesn't grow from a few kilobytes.
    /// </summary>
    private const long MaxUnpackedBytes = 2048L * 1024 * 1024;

    /// <summary>The file is a ZIP that holds <c>[Content_Types].xml</c> (Office Open XML) or <c>mimetype</c> (OpenDocument).</summary>
    public static bool IsPackage(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry(ContentTypes) is not null || zip.GetEntry(Mimetype) is not null;
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Writes every part of <paramref name="input"/> to <paramref name="output"/> with the same name and content:
    /// <c>[Content_Types].xml</c> first, an OpenDocument <c>mimetype</c> first and uncompressed (its signature),
    /// everything else deflated, without directory entries, data descriptors or extra fields. A name that appears
    /// twice keeps its first part.
    /// </summary>
    /// <param name="replaced">Parts written with other content (<see cref="LinkedContent"/>), by name.</param>
    /// <exception cref="ConversionRefusedException">The parts unpack to more than a document of this size can hold.</exception>
    public static void Repack(string input, string output, IReadOnlyDictionary<string, byte[]>? replaced = null) =>
        Repack(input, output, replaced, MaxUnpacked(input));

    /// <summary>What the package at <paramref name="path"/> may unpack to.</summary>
    internal static long MaxUnpacked(string path) => Math.Max(MaxUnpackedBytes, 2 * new FileInfo(path).Length);

    /// <inheritdoc cref="Repack(string, string, IReadOnlyDictionary{string, byte[]}?)"/>
    /// <param name="maxBytes">The limit (for tests).</param>
    internal static void Repack(string input, string output, IReadOnlyDictionary<string, byte[]>? replaced, long maxBytes)
    {
        using var source = ZipFile.OpenRead(input);
        var parts = source.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\'))
            .OrderBy(e => e.FullName is ContentTypes or Mimetype ? 0 : 1)
            .ToList();

        // A seekable stream: the sizes go into the local headers instead of data descriptors.
        using var file = File.Create(output);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[81920];
        long total = 0;
        foreach (var part in parts)
        {
            if (!written.Add(part.FullName))
                continue;
            var level = part.FullName == Mimetype ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            var copy = zip.CreateEntry(part.FullName, level);
            if (part.LastWriteTime.Year is >= 1980 and <= 2107)
                copy.LastWriteTime = part.LastWriteTime;
            using var to = copy.Open();
            if (replaced is not null && replaced.TryGetValue(part.FullName, out var content))
            {
                to.Write(content);
                continue;
            }
            using var from = part.Open();
            int count;
            while ((count = from.Read(buffer)) > 0)
            {
                total += count;
                if (total > maxBytes)
                    throw new ConversionRefusedException("error.document_too_large", $"{part.FullName}: the package unpacks to over {BoundedXml.Size(maxBytes)}");
                to.Write(buffer, 0, count);
            }
        }
    }
}
