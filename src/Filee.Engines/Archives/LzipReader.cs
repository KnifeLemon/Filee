// Reads lzip files (.lz, .tar.lz), which 7-Zip does not open. An lzip member is a 6-byte header ("LZIP", version 1,
// coded dictionary size), a raw LZMA stream (lc=3, lp=0, pb=2, always ended by an end marker) and a 20-byte
// trailer (CRC-32 u32, data size u64, member size u64). Each member's LZMA data is decoded in-process with
// SharpCompress's LZMA decoder (MIT) and checked against its trailer.

using SharpCompress.Compressors.LZMA;

namespace Filee.Engines.Archives;

/// <summary>Decompresses lzip files.</summary>
internal static class LzipReader
{
    private const int HeaderSize = 6;
    private const int TrailerSize = 20;

    /// <summary>Decompresses <paramref name="lzipFile"/> into <paramref name="outputFile"/>.</summary>
    public static void Decompress(string lzipFile, string outputFile, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(lzipFile);
        using var input = new FileStream(lzipFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var members = FindMembers(input, name);
        var folder = Path.GetDirectoryName(Path.GetFullPath(outputFile))!;
        var total = members.Sum(m => m.DataSize);
        ArchiveSafety.CheckRatio(total, input.Length, name);
        ArchiveSafety.EnsureRoom(total, folder, name);
        var budget = new UnpackBudget(name, input.Length, folder);

        try
        {
            using (var output = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                foreach (var member in members)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    input.Position = member.Start + 5;
                    var dictionary = DictionarySize(input.ReadByte());
                    // LZMA properties: lc=3, lp=0, pb=2, then the dictionary size.
                    byte[] properties = [0x5D, .. BitConverter.GetBytes(dictionary)];
                    var packed = member.Size - HeaderSize - TrailerSize;
                    var crc = new Crc32();
                    using (var decoder = new LzmaStream(properties, new BoundedStream(input, packed), packed, member.DataSize))
                    using (var checkedOutput = new CrcStream(new NonClosingStream(output), crc))
                    {
                        if (ArchiveFiles.CopyLimited(decoder, checkedOutput, member.DataSize, cancellationToken, budget: budget) != member.DataSize)
                            throw ArchiveSafety.Damaged(name, "wrong data size");
                    }
                    if (crc.Value != member.Crc)
                        throw ArchiveSafety.Damaged(name, "checksum error");
                }
            }
            File.SetLastWriteTimeUtc(outputFile, File.GetLastWriteTimeUtc(lzipFile));
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            ArchiveFiles.TryDeleteFile(outputFile);
            throw ArchiveSafety.Damaged(name, "LZMA data could not be decoded");
        }
        catch
        {
            ArchiveFiles.TryDeleteFile(outputFile);
            throw;
        }
    }

    /// <summary>One lzip member.</summary>
    private sealed record Member(long Start, long Size, uint Crc, long DataSize);

    /// <summary>
    /// Finds the members from the end of the file backwards (every trailer ends with its member's size), as lzip does.
    /// </summary>
    private static List<Member> FindMembers(FileStream input, string name)
    {
        var members = new List<Member>();
        var trailer = new byte[TrailerSize];
        var header = new byte[5];
        var end = input.Length;
        while (end > 0)
        {
            if (end < HeaderSize + TrailerSize)
                throw InvalidLzip(name);
            input.Position = end - TrailerSize;
            input.ReadExactly(trailer);
            var crc = BitConverter.ToUInt32(trailer, 0);
            var dataSize = BitConverter.ToInt64(trailer, 4);
            var size = BitConverter.ToInt64(trailer, 12);
            if (size < HeaderSize + TrailerSize || size > end || dataSize < 0)
                throw InvalidLzip(name);

            var start = end - size;
            input.Position = start;
            input.ReadExactly(header);
            if (header[0] != 'L' || header[1] != 'Z' || header[2] != 'I' || header[3] != 'P' || header[4] != 1)
                throw InvalidLzip(name);
            // lzip allows dictionaries of 4 KiB to 512 MiB; the decoder allocates the whole dictionary.
            var coded = input.ReadByte();
            if (coded < 0 || DictionarySize(coded) is < 1u << 12 or > 1u << 29)
                throw InvalidLzip(name);
            if (members.Count >= ArchiveSafety.EntryLimit)
                throw ArchiveSafety.TooManyEntries(name);
            members.Add(new Member(start, size, crc, dataSize));
            end = start;
        }
        if (members.Count == 0)
            throw InvalidLzip(name);
        members.Reverse();
        return members;
    }

    /// <summary>
    /// The coded dictionary size of the header: bits 0-4 are the base-2 logarithm, bits 5-7 subtract that many
    /// sixteenths.
    /// </summary>
    internal static uint DictionarySize(int coded)
    {
        var size = 1u << (coded & 0x1F);
        return size - (size / 16) * (uint)((coded >> 5) & 7);
    }

    private static InvalidDataException InvalidLzip(string name) =>
        new($"{name} is not a valid lzip file, or it is damaged.");
}
