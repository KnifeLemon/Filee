// Unpacks a compressed single file (.gz, .bz2, .xz, .lzma, .Z, .zst and the outer layer of .tar.gz, .tar.bz2, .tar.xz
// and .tar.Z) in-process: gzip with .NET's own decoder, bzip2, xz, LZMA and zstd with SharpCompress (MIT), Unix
// compress with Filee's own UnixCompressStream. None of these formats declares a size it must keep (gzip's is the
// size modulo 4 GiB), so what comes out is counted by UnpackBudget against bombs and a full drive.

using System.IO.Compression;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.ZStandard;
using SharpCompressMode = SharpCompress.Compressors.CompressionMode;

namespace Filee.Engines.Archives;

/// <summary>Decoders for compressed single files.</summary>
internal static class CompressedFile
{
    /// <summary>Formats (and TAR wrappers) <see cref="Decompress"/> reads.</summary>
    public static readonly IReadOnlySet<string> Formats =
        new HashSet<string> { "gz", "tgz", "bz2", "tbz2", "xz", "txz", "lzma", "z", "tz", "zst" };

    /// <summary>The decoder allocates the whole dictionary up front; real files use at most a few hundred MiB.</summary>
    internal const uint MaxLzmaDictionary = 1u << 30;

    /// <summary>
    /// Decompresses <paramref name="archive"/> into <paramref name="outputFile"/> (which must not exist).
    /// </summary>
    public static void Decompress(string archive, string format, string outputFile, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(archive);
        using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var budget = new UnpackBudget(name, input.Length, Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
        try
        {
            long declared = -1;
            using (var decoder = Open(format, input, name, ref declared))
            using (var output = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var written = ArchiveFiles.CopyLimited(decoder, output, declared >= 0 ? declared : long.MaxValue, cancellationToken, budget: budget);
                if (declared >= 0 && written != declared)
                    throw ArchiveSafety.Damaged(name, "the data is shorter than its header says");
            }
            File.SetLastWriteTimeUtc(outputFile, File.GetLastWriteTimeUtc(archive));
        }
        catch
        {
            ArchiveFiles.TryDeleteFile(outputFile);
            throw;
        }
    }

    /// <summary>
    /// A decoder reading <paramref name="input"/>; <paramref name="declared"/> becomes the unpacked size when the
    /// header names one (.lzma).
    /// </summary>
    internal static Stream Open(string format, Stream input, string name, ref long declared)
    {
        switch (format)
        {
            case "gz":
            case "tgz":
                return new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            case "bz2":
            case "tbz2":
                return new BZip2Stream(new NonClosingStream(input), SharpCompressMode.Decompress, decompressConcatenated: true);
            case "xz":
            case "txz":
                return new XZStream(new NonClosingStream(input));
            case "z":
            case "tz":
                return new UnixCompressStream(new NonClosingStream(input));
            case "zst":
                return new DecompressionStream(new NonClosingStream(input));
            case "lzma":
                {
                    // "LZMA alone": properties (lc/lp/pb, dictionary size), unpacked size u64 (all ones: not known,
                    // the data ends with an end marker), raw LZMA data.
                    var header = new byte[13];
                    if (input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || header[0] >= 9 * 5 * 5)
                        throw ArchiveSafety.NotAnArchive(name);
                    if (BitConverter.ToUInt32(header, 1) > MaxLzmaDictionary)
                        throw ArchiveSafety.Damaged(name, "the LZMA dictionary is larger than 1 GiB");
                    var size = BitConverter.ToInt64(header, 5);
                    declared = size;
                    return new LzmaStream(header[..5], new NonClosingStream(input), input.Length - input.Position, size < 0 ? -1 : size);
                }
            default:
                throw new NotSupportedException($"{format} is not a compressed single file.");
        }
    }

    /// <summary>
    /// The file a compressed single file unpacks to: its name without the compression extension, and ".tar" for the
    /// TAR wrappers ("backup.tgz" → "backup.tar").
    /// </summary>
    public static string OutputName(string archive, bool tar)
    {
        var stem = Core.Formats.FormatRegistry.NameWithoutExtension(archive);
        if (stem.Length == 0)
            stem = "data";
        return tar && !stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? stem + ".tar" : stem;
    }
}
