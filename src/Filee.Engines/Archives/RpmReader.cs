// Takes the payload out of an RPM package: a 96-byte lead (magic ED AB EE DB), the signature header (magic 8E AD E8 01,
// 4 reserved bytes, index count u32 and data size u32 big endian, 16 bytes per index entry, the data, padded to a
// multiple of 8), the main header (same layout, not padded), then the payload: a cpio archive, usually compressed
// with gzip, bzip2, xz, LZMA or zstd (told by its first bytes). The payload is decompressed into "<name>.cpio", which
// the next stage unpacks. Written from the RPM file format description (rpm.org).

using System.Buffers.Binary;

namespace Filee.Engines.Archives;

/// <summary>Unpacks the cpio payload of RPM packages.</summary>
internal static class RpmReader
{
    private const int LeadSize = 96;
    private const int MaxIndexEntries = 1 << 20;
    private const int MaxHeaderData = 256 * 1024 * 1024;

    /// <summary>Writes the decompressed payload of <paramref name="archive"/> into <paramref name="folder"/>.</summary>
    public static void Extract(string archive, string folder, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(archive);
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        Span<byte> lead = stackalloc byte[LeadSize];
        if (stream.ReadAtLeast(lead, LeadSize, throwOnEndOfStream: false) < LeadSize || BinaryPrimitives.ReadUInt32BigEndian(lead) != 0xEDABEEDB)
            throw ArchiveSafety.NotAnArchive(name);
        SkipHeader(stream, name, padded: true);  // signature
        SkipHeader(stream, name, padded: false); // main header

        Span<byte> magic = stackalloc byte[6];
        var payload = stream.Position;
        var read = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        stream.Position = payload;
        var format = read < 6 ? null : FormatOf(magic);
        if (format is null)
            throw ArchiveSafety.Damaged(name, "the package payload is in an unknown format");

        var output = Path.Combine(folder, CompressedFile.OutputName(archive, tar: false) + ".cpio");
        var budget = new UnpackBudget(name, stream.Length, folder);
        try
        {
            long declared = -1;
            using (var data = format == "cpio" ? new BoundedStream(stream, stream.Length - payload) : CompressedFile.Open(format, stream, name, ref declared))
            using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                ArchiveFiles.CopyLimited(data, file, declared >= 0 ? declared : long.MaxValue, cancellationToken, budget: budget);
        }
        catch
        {
            ArchiveFiles.TryDeleteFile(output);
            throw;
        }
    }

    private static void SkipHeader(FileStream stream, string name, bool padded)
    {
        Span<byte> header = stackalloc byte[16];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length
            || header[0] != 0x8E || header[1] != 0xAD || header[2] != 0xE8 || header[3] != 0x01)
            throw ArchiveSafety.Damaged(name, "invalid RPM header");
        var count = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        var size = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        if (count > MaxIndexEntries || size > MaxHeaderData)
            throw ArchiveSafety.Damaged(name, "the RPM header is too large");
        var end = stream.Position + count * 16L + size;
        if (padded)
            end = (end + 7) / 8 * 8;
        if (end > stream.Length)
            throw ArchiveSafety.Damaged(name, "the package ends inside its header");
        stream.Position = end;
    }

    private static string? FormatOf(ReadOnlySpan<byte> magic) => magic switch
    {
        [0x1F, 0x8B, ..] => "gz",
        [(byte)'B', (byte)'Z', (byte)'h', ..] => "bz2",
        [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00] => "xz",
        [0x28, 0xB5, 0x2F, 0xFD, ..] => "zst",
        [0x5D, 0x00, 0x00, ..] => "lzma",
        [(byte)'0', (byte)'7', (byte)'0', (byte)'7', (byte)'0', _] => "cpio",
        _ => null,
    };
}
