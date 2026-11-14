// Reads NSIS installers (Nullsoft Scriptable Install System: Tesseract's Windows build and many other setup.exe files)
// without running them, and writes the files their script installs, named as 7-Zip names them. Filee's reader is its
// own, written from the format description. Little-endian structures:
//
//   stub          the installer program (a PE file); not read
//   first header  at a multiple of 512 bytes: flags u32 (1 uninstaller, 2 silent, 4 no CRC check, 8 force the check),
//                 0xDEADBEEF, "NullsoftInst", size of the header block once unpacked u32, size of everything from the
//                 first header on u32 (the CRC included)
//   data          solid: one compressed stream holding [size u32][header block], then [size u32][data] per file.
//                 Not solid: the header block, then the data blocks, each [size u32, bit 31 set when compressed][data].
//   CRC-32        of the file from offset 512 up to here (checked unless flag 4 is set)
//
// The compression (one per installer) is told by the first bytes, as NSIS writes no method id: LZMA starts with its
// properties (0x5D, the dictionary size) and ends with an end marker, sometimes after a filter byte (1: the x86 branch
// converter BCJ); NSIS's bzip2 starts a block with 0x31 and the block's origPtr (see Bzip2Decoder); anything else is
// deflate without a zlib header. A data block NSIS could not shrink is stored.
//
// The header block starts with flags u32 and blocks (offset u32, count u32) for pages, sections, entries, strings,
// language tables …; Filee reads the entries (the compiled script: opcode u32 and 6 parameters u32 each) and the
// strings. It follows, in the entries' order and without taking jumps, SetOutPath (EW_CREATEDIR, opcode 11, with
// parameter 1 set: the folder relative names go to), File (EW_EXTRACTFILE, 20: name, data offset, FILETIME) and in
// NSIS 3 WriteUninstaller (62: name, data offset; the data is the finished uninstaller and is written as it is). The
// same name with the same data (a plug-in DLL extracted before every call) is one file. Strings are UTF-16 (NSIS 3
// Unicode) or bytes (NSIS 2 and NSIS 3 ANSI, read as Latin-1) with codes for variables ($INSTDIR, $PLUGINSDIR …),
// shell folders and language strings, followed by a number; codes 1-4 in NSIS 3, 252-255 in NSIS 2 (an ANSI table is
// NSIS 3's when more variables and language strings are coded the NSIS 3 way). Names keep their variables
// ("$PLUGINSDIR\System.dll"), except $OUTDIR, which is the current SetOutPath, and a leading $INSTDIR, which is dropped:
// "$INSTDIR\bin\a.dll" is "bin\a.dll". NSIS 1.x scripts (other opcodes) are not read.
//
// Checked against 7-Zip 26.05 on Tesseract's installer (solid LZMA, NSIS 3 Unicode): the same 139 files, byte for byte,
// with the same times (NsisReaderTests). The CRC starting at offset 512 was found on that installer too.
//
// Hostile input: the header block is read only up to 64 MB and grows as data arrives; every count and offset is checked
// against the bytes it points into; what is decoded (skipped data too) counts against the bomb ratio and what is
// written goes through UnpackBudget; names go through CheckListing and ResolveEntryPath like every reader's.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;
using Filee.Engines.Archives.Lzma;

namespace Filee.Engines.Archives;

/// <summary>How an NSIS installer is compressed.</summary>
internal enum NsisCompression
{
    Stored,
    Deflate,
    Bzip2,
    Lzma,
}

/// <summary>What <see cref="NsisReader.Extract"/> writes.</summary>
internal enum NsisScope
{
    /// <summary>Every file the script writes, named as 7-Zip names them ("$PLUGINSDIR\System.dll").</summary>
    Everything,

    /// <summary>The files that go into $INSTDIR: the program as the installer would install it.</summary>
    InstallDir,
}

/// <summary>One file an NSIS installer writes.</summary>
/// <param name="Path">"bin\a.dll" for $INSTDIR\bin\a.dll; other places keep their variable ("$PLUGINSDIR\a.dll").</param>
/// <param name="Offset">Where its data block starts, counted from the end of the header block.</param>
/// <param name="InInstallDir">It goes into $INSTDIR (or, before any SetOutPath, into the current folder).</param>
/// <param name="IsUninstaller">Written by WriteUninstaller.</param>
internal sealed record NsisFile(string Path, long Offset, DateTime? Modified, bool InInstallDir, bool IsUninstaller = false);

/// <summary>What <see cref="NsisReader.List"/> finds in an installer.</summary>
/// <param name="Unicode">The strings are UTF-16 (NSIS 3 Unicode).</param>
internal sealed record NsisListing(NsisCompression Compression, bool Solid, bool Unicode, IReadOnlyList<NsisFile> Files);

/// <summary>Lists and unpacks NSIS installers (see the file comment).</summary>
internal static class NsisReader
{
    private const int FirstHeaderSize = 28;
    private const uint Signature = 0xDEADBEEF;
    private const uint FlagNoCrc = 4;
    private const int Alignment = 512;
    private const int CrcStart = 512;
    private const uint CompressedBit = 0x80000000;

    /// <summary>The header block's flags and its 8 blocks (offset, count).</summary>
    private const int MinHeaderBytes = 4 + 8 * 8;

    private const int EntrySize = 28;
    private const int OpSetOutPath = 11;
    private const int OpExtractFile = 20;
    private const int OpWriteUninstaller = 62;

    /// <summary>The largest header block read: installers of tens of thousands of files stay below a few megabytes.</summary>
    public const int MaxHeaderBytes = 64 << 20;

    /// <summary>Reads the script of <paramref name="installer"/>: the files it writes.</summary>
    public static NsisListing List(string installer, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(installer);
        try
        {
            using var image = Image.Open(installer, name, cancellationToken);
            return image.Listing;
        }
        catch (EndOfStreamException)
        {
            throw ArchiveSafety.Damaged(name, "the installer ends early");
        }
    }

    /// <summary>
    /// Writes the files of <paramref name="installer"/> that <paramref name="scope"/> names into
    /// <paramref name="folder"/>, byte for byte as the installer would write them. Returns the listing.
    /// </summary>
    public static NsisListing Extract(string installer, string folder, NsisScope scope, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(installer);
        try
        {
            using var image = Image.Open(installer, name, cancellationToken);
            image.Write(folder, scope, cancellationToken);
            return image.Listing;
        }
        catch (EndOfStreamException)
        {
            throw ArchiveSafety.Damaged(name, "the installer ends early");
        }
    }

    /// <summary>True for a name that doesn't go into the current folder: a variable, a drive or a root.</summary>
    internal static bool IsRooted(string path) =>
        path.Length > 0 && (path[0] is '$' or '\\' or '/' || path.Length >= 2 && path[1] == ':');

    private static uint U32(ReadOnlySpan<byte> bytes, long at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)at..]);

    private static uint ReadU32(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    /// <summary>An open installer: where its parts are, its script, and the decoder of a solid one.</summary>
    private sealed class Image : IDisposable
    {
        private readonly FileStream _file;
        private readonly string _name;
        private readonly long _dataEnd;
        private readonly Tally _tally;

        /// <summary>A solid installer's stream, positioned after the header block.</summary>
        private Stream? _solid;

        /// <summary>Where a non-solid installer's data blocks start (offset 0 of <see cref="NsisFile.Offset"/>).</summary>
        private long _blocks;

        private Image(FileStream file, string name, long dataEnd, CancellationToken cancellationToken)
        {
            _file = file;
            _name = name;
            _dataEnd = dataEnd;
            _tally = new Tally(name, file.Length, cancellationToken);
        }

        public NsisListing Listing { get; private set; } = null!;

        public static Image Open(string path, string name, CancellationToken cancellationToken)
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            Image? image = null;
            try
            {
                var (dataStart, dataEnd, headerLength) = FindFirstHeader(file, name, cancellationToken);
                image = new Image(file, name, dataEnd, cancellationToken);
                image.ReadHeader(dataStart, headerLength, cancellationToken);
                return image;
            }
            catch
            {
                // Stops a decoder thread before the file closes under it.
                if (image is not null)
                    image.Dispose();
                else
                    file.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _solid?.Dispose();
            _file.Dispose();
        }

        /// <summary>
        /// Finds the first header (at a multiple of 512 bytes, as NSIS itself looks for it), checks the sizes and the
        /// CRC, and returns where the data lies and the size of the header block.
        /// </summary>
        private static (long DataStart, long DataEnd, int HeaderLength) FindFirstHeader(FileStream file, string name, CancellationToken cancellationToken)
        {
            var buffer = new byte[1 << 20];
            for (long chunk = 0; chunk < file.Length; chunk += buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                file.Position = chunk;
                var read = file.ReadAtLeast(buffer, (int)Math.Min(buffer.Length, file.Length - chunk), throwOnEndOfStream: false);
                for (var at = 0; at + FirstHeaderSize <= read; at += Alignment)
                {
                    var header = buffer.AsSpan(at, FirstHeaderSize);
                    if (U32(header, 4) != Signature || !header.Slice(8, 12).SequenceEqual("NullsoftInst"u8))
                        continue;
                    var start = chunk + at;
                    var flags = U32(header, 0);
                    var headerLength = U32(header, 20);
                    long total = U32(header, 24);
                    if (headerLength > MaxHeaderBytes)
                        throw ArchiveSafety.Oversized(name,
                            $"its NSIS header unpacks to {ArchiveSafety.FormatSize(headerLength)}, more than the {ArchiveSafety.FormatSize(MaxHeaderBytes)} Filee reads");
                    if (headerLength < MinHeaderBytes || total < FirstHeaderSize + 4)
                        throw ArchiveSafety.Damaged(name, "the NSIS first header is invalid");
                    if (start + total > file.Length)
                        throw ArchiveSafety.Damaged(name, "the installer is cut short");
                    var end = start + total;
                    if ((flags & FlagNoCrc) != 0 || start < CrcStart)
                        return (start + FirstHeaderSize, end, (int)headerLength);
                    CheckCrc(file, CrcStart, end - 4, name, cancellationToken);
                    return (start + FirstHeaderSize, end - 4, (int)headerLength);
                }
            }
            throw ArchiveSafety.NotAnArchive(name);
        }

        private static void CheckCrc(FileStream file, long from, long to, string name, CancellationToken cancellationToken)
        {
            var crc = new Crc32();
            var buffer = new byte[1 << 20];
            file.Position = from;
            for (var left = to - from; left > 0;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read == 0)
                    throw new EndOfStreamException();
                crc.Append(buffer.AsSpan(0, read));
                left -= read;
            }
            if (crc.Value != ReadU32(file))
                throw ArchiveSafety.Damaged(name, "CRC error in the installer");
        }

        /// <summary>Tells the compression, reads the header block and the script.</summary>
        private void ReadHeader(long dataStart, int headerLength, CancellationToken cancellationToken)
        {
            var start = Peek(dataStart, 16);
            if (start.Length < 4)
                throw ArchiveSafety.Damaged(_name, "the installer has no data");
            var first = U32(start, 0);
            NsisCompression compression;
            bool solid;
            byte[] header;
            if (first == headerLength)
            {
                // Not solid, the header block stored as it is.
                compression = NsisCompression.Stored;
                solid = false;
                if (dataStart + 4 + headerLength > _dataEnd)
                    throw ArchiveSafety.Damaged(_name, "the NSIS header block lies past the end of the data");
                header = ReadBlock(Bounded(dataStart + 4, headerLength), headerLength);
                _blocks = dataStart + 4 + headerLength;
            }
            else if (IsLzma(start, out var filter))
            {
                compression = NsisCompression.Lzma;
                solid = true;
                header = ReadSolid(dataStart, compression, filter, headerLength, cancellationToken);
            }
            else if ((first & CompressedBit) != 0)
            {
                solid = false;
                var packed = first & ~CompressedBit;
                if (dataStart + 4 + packed > _dataEnd)
                    throw ArchiveSafety.Damaged(_name, "the NSIS header block lies past the end of the data");
                (compression, filter) = Sniff(start.AsSpan(4));
                using (var decoder = Decoder(Bounded(dataStart + 4, packed), compression, filter, cancellationToken))
                    header = ReadBlock(decoder, headerLength);
                _blocks = dataStart + 4 + packed;
            }
            else
            {
                compression = IsBzip2(start) ? NsisCompression.Bzip2 : NsisCompression.Deflate;
                solid = true;
                header = ReadSolid(dataStart, compression, false, headerLength, cancellationToken);
            }
            var script = new Script(header, _name, cancellationToken);
            Listing = new NsisListing(compression, solid, script.Unicode, script.Files);
        }

        /// <summary>Opens the solid stream and reads its header block; the stream stays open for the files.</summary>
        private byte[] ReadSolid(long dataStart, NsisCompression compression, bool filter, int headerLength, CancellationToken cancellationToken)
        {
            _solid = Decoder(Bounded(dataStart, _dataEnd - dataStart), compression, filter, cancellationToken);
            if (ReadU32(_solid) != headerLength)
                throw ArchiveSafety.Damaged(_name, "the NSIS header block has another size than the first header says");
            return ReadBlock(_solid, headerLength);
        }

        /// <summary>
        /// The first <paramref name="length"/> bytes of <paramref name="stream"/>; the buffer grows as they arrive, so a
        /// size that lies costs no memory.
        /// </summary>
        private byte[] ReadBlock(Stream stream, int length)
        {
            var buffer = new byte[Math.Min(length, 1 << 16)];
            var filled = 0;
            while (filled < length)
            {
                if (filled == buffer.Length)
                    Array.Resize(ref buffer, (int)Math.Min(length, buffer.Length * 2L));
                var read = stream.Read(buffer, filled, buffer.Length - filled);
                if (read == 0)
                    throw ArchiveSafety.Damaged(_name, "the NSIS header block ends early");
                filled += read;
            }
            return buffer;
        }

        /// <summary>Up to <paramref name="count"/> bytes from <paramref name="at"/>, not past the data.</summary>
        private byte[] Peek(long at, int count)
        {
            var bytes = new byte[(int)Math.Clamp(_dataEnd - at, 0, count)];
            _file.Position = at;
            _file.ReadExactly(bytes);
            return bytes;
        }

        /// <summary>The file from <paramref name="at"/> on, <paramref name="length"/> bytes.</summary>
        private BoundedStream Bounded(long at, long length)
        {
            _file.Position = at;
            return new BoundedStream(_file, length);
        }

        /// <summary>The decoder of one compressed stream, counted against the bomb ratio.</summary>
        private Stream Decoder(Stream packed, NsisCompression compression, bool filter, CancellationToken cancellationToken)
        {
            Stream decoder;
            switch (compression)
            {
                case NsisCompression.Deflate:
                    decoder = new DeflateStream(packed, CompressionMode.Decompress, leaveOpen: true);
                    break;
                case NsisCompression.Bzip2:
                    decoder = new ProducedStream((output, token) => Bzip2Decoder.DecompressNsis(packed, output, long.MaxValue, token), cancellationToken);
                    break;
                case NsisCompression.Lzma:
                    var x86 = false;
                    if (filter)
                    {
                        var flag = packed.ReadByte();
                        if (flag is not (0 or 1))
                            throw ArchiveSafety.Damaged(_name, "unknown NSIS filter");
                        x86 = flag == 1;
                    }
                    Span<byte> header = stackalloc byte[5];
                    packed.ReadExactly(header);
                    var properties = LzmaProperties.FromByte(header[0]) ?? throw ArchiveSafety.Damaged(_name, "invalid LZMA properties");
                    var dictionary = U32(header, 1);
                    if (dictionary > LzmaDecoder.MaxDictionary)
                        throw ArchiveSafety.Oversized(_name,
                            $"it needs a {ArchiveSafety.FormatSize(dictionary)} dictionary, more than the {ArchiveSafety.FormatSize(LzmaDecoder.MaxDictionary)} Filee allows");
                    decoder = new LzmaRawReader(packed, properties, dictionary, -1);
                    if (x86)
                        decoder = new BcjStream(decoder, 0);
                    break;
                default:
                    return packed;
            }
            return new Counted(decoder, _tally);
        }

        /// <summary>Writes the files <paramref name="scope"/> names.</summary>
        public void Write(string folder, NsisScope scope, CancellationToken cancellationToken)
        {
            var files = Listing.Files.Where(f => scope == NsisScope.Everything || f.InInstallDir).ToList();
            Directory.CreateDirectory(folder);
            // Sizes are known only once decoded: the listing checks names, paths and counts, the budget the bytes.
            ArchiveSafety.CheckListing([.. files.Select(f => new ArchiveItem(f.Path, false, 0))], _name, _file.Length, folder);
            CheckFolders(files, folder);
            var writer = new EntryWriter(folder, new UnpackBudget(_name, _file.Length, folder), cancellationToken);
            long position = 0; // in a solid stream, after the header block
            foreach (var group in files.GroupBy(f => f.Offset).OrderBy(g => g.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = group.First();
                string written;
                if (_solid is { } solid)
                {
                    if (group.Key < position)
                        throw ArchiveSafety.Damaged(_name, $"the data of {first.Path} overlaps another file's");
                    Skip(solid, group.Key - position);
                    var size = ReadU32(solid);
                    if (size > int.MaxValue)
                        throw ArchiveSafety.Damaged(_name, $"{first.Path} has an impossible size");
                    written = writer.AddFile(first.Path, new BoundedStream(solid, size), size, null, first.Modified);
                    position = group.Key + 4 + size;
                }
                else
                {
                    written = WriteBlock(writer, first, cancellationToken);
                }
                // The same data under other names: copies of the file just written.
                foreach (var other in group.Skip(1))
                {
                    using var copy = new FileStream(written, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    writer.AddFile(other.Path, copy, copy.Length, null, other.Modified);
                }
            }
            writer.Finish();
        }

        /// <summary>
        /// Refuses a name that is a file and also the folder of another file: the script would overwrite one with the
        /// other. The names are compared as the writer makes paths of them (ResolveEntryPath).
        /// </summary>
        private void CheckFolders(List<NsisFile> files, string folder)
        {
            var root = Path.GetFullPath(folder);
            var paths = files.Select(f => ArchiveSafety.ResolveEntryPath(root, f.Path, _name)).ToList();
            var asFiles = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                for (var parent = Path.GetDirectoryName(path); parent is not null && parent.Length > root.Length; parent = Path.GetDirectoryName(parent))
                {
                    if (asFiles.Contains(parent))
                        throw ArchiveSafety.Damaged(_name, $"{Path.GetRelativePath(root, parent)} is a file and a folder");
                }
            }
        }

        /// <summary>Writes one data block of a non-solid installer.</summary>
        private string WriteBlock(EntryWriter writer, NsisFile file, CancellationToken cancellationToken)
        {
            var at = _blocks + file.Offset;
            if (at + 4 > _dataEnd)
                throw ArchiveSafety.Damaged(_name, $"the data of {file.Path} lies past the end of the installer");
            _file.Position = at;
            var size = ReadU32(_file);
            var length = size & ~CompressedBit;
            if (at + 4 + length > _dataEnd)
                throw ArchiveSafety.Damaged(_name, $"the data of {file.Path} lies past the end of the installer");
            if ((size & CompressedBit) == 0)
                return writer.AddFile(file.Path, Bounded(at + 4, length), length, null, file.Modified);
            var (compression, filter) = Sniff(Peek(at + 4, 8));
            using var decoder = Decoder(Bounded(at + 4, length), compression, filter, cancellationToken);
            return writer.AddFile(file.Path, decoder, -1, null, file.Modified);
        }

        private void Skip(Stream stream, long count)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
            try
            {
                while (count > 0)
                {
                    var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                    if (read == 0)
                        throw ArchiveSafety.Damaged(_name, "a file's data lies past the end of the installer");
                    count -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>The compression of a non-solid data block, from its first bytes.</summary>
    private static (NsisCompression Compression, bool Filter) Sniff(ReadOnlySpan<byte> start)
    {
        if (IsLzma(start, out var filter))
            return (NsisCompression.Lzma, filter);
        return (IsBzip2(start) ? NsisCompression.Bzip2 : NsisCompression.Deflate, false);
    }

    /// <summary>
    /// LZMA as NSIS writes it: properties 0x5D (lc 3, lp 0, pb 2), a dictionary of whole kilobytes, then the range
    /// coder's first byte, always 0; with <paramref name="filter"/>, after a filter byte (0 none, 1 BCJ).
    /// </summary>
    private static bool IsLzma(ReadOnlySpan<byte> start, out bool filter)
    {
        static bool Starts(ReadOnlySpan<byte> p) => p.Length >= 6 && p[0] == 0x5D && p[1] == 0 && p[5] == 0;
        filter = start.Length > 0 && start[0] <= 1 && Starts(start[1..]);
        return filter || Starts(start);
    }

    /// <summary>NSIS's bzip2: the block marker 0x31, then the top byte of origPtr, below 900,000.</summary>
    private static bool IsBzip2(ReadOnlySpan<byte> start) => start.Length >= 2 && start[0] == 0x31 && start[1] < 14;

    /// <summary>The script: the files the entries write, with names from the string table.</summary>
    private sealed class Script
    {
        private readonly Strings _strings;
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _name;

        public Script(byte[] header, string name, CancellationToken cancellationToken)
        {
            _name = name;
            var (entriesAt, entryCount) = Block(header, 2);
            var (stringsAt, _) = Block(header, 3);
            var (languagesAt, _) = Block(header, 4);
            if (entriesAt + entryCount * EntrySize > header.Length)
                throw ArchiveSafety.Damaged(name, "the NSIS script lies past the end of its header");
            var stringsEnd = languagesAt > stringsAt && languagesAt <= header.Length ? languagesAt : header.Length;
            if (stringsAt >= stringsEnd)
                throw ArchiveSafety.Damaged(name, "the NSIS string table lies past the end of its header");
            _strings = new Strings(header.AsMemory((int)stringsAt, (int)(stringsEnd - stringsAt)), name);
            Unicode = _strings.Unicode;

            var outDir = "";
            var end = entriesAt + entryCount * EntrySize;
            for (var at = entriesAt; at < end; at += EntrySize)
            {
                if ((at - entriesAt) % (EntrySize << 12) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var opcode = U32(header, at);
                int Parameter(int index) => (int)U32(header, at + 4 + 4 * index);
                switch (opcode)
                {
                    case OpSetOutPath when Parameter(1) != 0:
                        outDir = _strings.Get(Parameter(0), outDir);
                        break;
                    case OpExtractFile:
                        Add(_strings.Get(Parameter(1), outDir), outDir, Parameter(2), Time(Parameter(3), Parameter(4)), false);
                        break;
                    case OpWriteUninstaller when _strings.Nsis3:
                        Add(_strings.Get(Parameter(0), outDir), outDir, Parameter(1), null, true);
                        break;
                }
            }
        }

        public bool Unicode { get; }

        public List<NsisFile> Files { get; } = [];

        private static (long Offset, long Count) Block(byte[] header, int index) =>
            (U32(header, 4 + 8 * index), U32(header, 8 + 8 * index));

        private void Add(string name, string outDir, int offset, DateTime? modified, bool uninstaller)
        {
            if (offset < 0)
                throw ArchiveSafety.Damaged(_name, "an NSIS file has a negative data offset");
            var path = IsRooted(name) || outDir.Length == 0 ? name : outDir.TrimEnd('\\', '/') + "\\" + name;
            var inInstallDir = !IsRooted(path);
            if (path.StartsWith("$INSTDIR", StringComparison.Ordinal) && (path.Length == 8 || path[8] is '\\' or '/'))
            {
                path = path[8..].TrimStart('\\', '/');
                inInstallDir = true;
            }
            if (path.Length == 0 || !_seen.Add($"{offset}|{path}"))
                return;
            if (Files.Count >= ArchiveSafety.EntryLimit)
                throw ArchiveSafety.TooManyEntries(_name);
            Files.Add(new NsisFile(path, offset, modified, inInstallDir, uninstaller));
        }

        /// <summary>A FILETIME (UTC) from two parameters; both -1 is "none".</summary>
        private static DateTime? Time(int low, int high)
        {
            var value = (long)(((ulong)(uint)high << 32) | (uint)low);
            if (low == -1 && high == -1 || value < 0 || value > DateTime.MaxValue.ToFileTimeUtc())
                return null;
            return DateTime.FromFileTimeUtc(value);
        }
    }

    /// <summary>The string table: UTF-16 or byte strings with codes for variables, shell folders and language strings.</summary>
    private sealed class Strings
    {
        private static readonly string[] Variables =
            ["CMDLINE", "INSTDIR", "OUTDIR", "EXEDIR", "LANGUAGE", "TEMP", "PLUGINSDIR", "EXEPATH", "EXEFILE", "HWNDPARENT", "_CLICK", "_OUTDIR"];

        private const int OutDirVariable = 22;

        /// <summary>
        /// Shell folders by CSIDL. NSIS uses 3, 4 and 10 (Control Panel, Printers, Recycle Bin) for $PROGRAMFILES,
        /// $QUICKLAUNCH and $COMMONFILES.
        /// </summary>
        private static readonly Dictionary<int, string> ShellFolders = new()
        {
            [0x00] = "DESKTOP",
            [0x02] = "SMPROGRAMS",
            [0x03] = "PROGRAMFILES",
            [0x04] = "QUICKLAUNCH",
            [0x05] = "DOCUMENTS",
            [0x06] = "FAVORITES",
            [0x07] = "SMSTARTUP",
            [0x08] = "RECENT",
            [0x09] = "SENDTO",
            [0x0A] = "COMMONFILES",
            [0x0B] = "STARTMENU",
            [0x0D] = "MUSIC",
            [0x0E] = "VIDEOS",
            [0x10] = "DESKTOP",
            [0x13] = "NETHOOD",
            [0x14] = "FONTS",
            [0x15] = "TEMPLATES",
            [0x16] = "STARTMENU",
            [0x17] = "SMPROGRAMS",
            [0x18] = "SMSTARTUP",
            [0x19] = "DESKTOP",
            [0x1A] = "APPDATA",
            [0x1B] = "PRINTHOOD",
            [0x1C] = "LOCALAPPDATA",
            [0x1F] = "FAVORITES",
            [0x20] = "INTERNET_CACHE",
            [0x21] = "COOKIES",
            [0x22] = "HISTORY",
            [0x23] = "APPDATA",
            [0x24] = "WINDIR",
            [0x25] = "SYSDIR",
            [0x26] = "PROGRAMFILES",
            [0x27] = "PICTURES",
            [0x28] = "PROFILE",
            [0x2B] = "COMMONFILES",
            [0x2D] = "TEMPLATES",
            [0x2E] = "DOCUMENTS",
            [0x2F] = "ADMINTOOLS",
            [0x30] = "ADMINTOOLS",
            [0x35] = "MUSIC",
            [0x36] = "PICTURES",
            [0x37] = "VIDEOS",
            [0x38] = "RESOURCES",
            [0x39] = "RESOURCES_LOCALIZED",
            [0x3B] = "CDBURN_AREA",
        };

        private readonly ReadOnlyMemory<byte> _table;
        private readonly string _name;
        private readonly int _language, _shell, _variable, _skip;

        public Strings(ReadOnlyMemory<byte> table, string name)
        {
            _table = table;
            _name = name;
            var bytes = table.Span;
            // The table starts with the empty string: one zero byte, or two in UTF-16. The next string is never empty.
            Unicode = bytes.Length >= 2 && bytes.Length % 2 == 0 && bytes[0] == 0 && bytes[1] == 0;
            // ANSI: NSIS 3 codes are 1-4, NSIS 2's 252-255. Both can be letters or shell folder numbers too, so the
            // codes that take a number (variables, language strings: two bytes with bit 7 set) are counted.
            Nsis3 = Unicode || NumberedCodes(bytes, 1, 3) > NumberedCodes(bytes, 255, 253);
            (_language, _shell, _variable, _skip) = Nsis3 ? (1, 2, 3, 4) : (255, 254, 253, 252);
        }

        private static int NumberedCodes(ReadOnlySpan<byte> bytes, byte language, byte variable)
        {
            var count = 0;
            for (var i = 0; i + 2 < bytes.Length; i++)
                if ((bytes[i] == language || bytes[i] == variable) && bytes[i + 1] >= 0x80 && bytes[i + 2] >= 0x80)
                    count++;
            return count;
        }

        public bool Unicode { get; }

        /// <summary>Written by NSIS 3 (Unicode, or ANSI with NSIS 3 codes).</summary>
        public bool Nsis3 { get; }

        /// <summary>The string at <paramref name="offset"/> (in characters); $OUTDIR becomes <paramref name="outDir"/>.</summary>
        public string Get(int offset, string outDir)
        {
            if (offset < 0)
                return $"$(LSTR_{-(offset + 1)})"; // a language string, as NSIS refers to them
            var width = Unicode ? 2 : 1;
            var length = _table.Length;
            var at = (long)offset * width;
            if (at >= length)
                throw ArchiveSafety.Damaged(_name, "an NSIS string lies outside the string table");
            var text = new StringBuilder();
            int Next()
            {
                if (at + width > length)
                    throw ArchiveSafety.Damaged(_name, "an NSIS string has no end");
                var bytes = _table.Span;
                int value = Unicode ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[(int)at..]) : bytes[(int)at];
                at += width;
                return value;
            }
            while (true)
            {
                var c = Next();
                if (c == 0)
                    return text.ToString();
                if (c == _skip)
                {
                    text.Append((char)Next());
                }
                else if (c == _variable || c == _language || c == _shell)
                {
                    // The number: two bytes with bit 7 set, as one UTF-16 unit in Unicode strings.
                    int low, high;
                    if (Unicode)
                    {
                        var unit = Next();
                        (low, high) = (unit & 0xFF, unit >> 8);
                    }
                    else
                    {
                        (low, high) = (Next(), Next());
                    }
                    var number = (low & 0x7F) | (high & 0x7F) << 7;
                    if (c == _variable)
                        text.Append(number == OutDirVariable && outDir.Length > 0 ? outDir : Variable(number));
                    else if (c == _language)
                        text.Append($"$(LSTR_{number})");
                    else
                        text.Append(Shell(low, high));
                }
                else
                {
                    text.Append((char)c); // Latin-1 for ANSI strings
                }
                if (text.Length > ArchiveSafety.MaxPathLength)
                    throw ArchiveSafety.NameTooLong(_name);
            }
        }

        private static string Variable(int number) => number switch
        {
            < 10 => "$" + number,
            < 20 => "$R" + (number - 10),
            < 32 => "$" + Variables[number - 20],
            _ => $"$_{number - 32}_",
        };

        /// <summary>A shell folder: the CSIDL for the current user, then the one for all users.</summary>
        private static string Shell(int current, int allUsers) =>
            ShellFolders.TryGetValue(current, out var name) || ShellFolders.TryGetValue(allUsers, out name)
                ? "$" + name
                : $"$SHELL_{current:X2}_{allUsers:X2}";
    }

    /// <summary>Counts what the decoders put out (skipped data too) against the bomb ratio.</summary>
    private sealed class Tally(string name, long packed, CancellationToken cancellationToken)
    {
        private long _total;

        public void Add(int bytes)
        {
            _total += bytes;
            ArchiveSafety.CheckRatio(_total, packed, name);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>A decoder whose output goes through <see cref="Tally"/>.</summary>
    private sealed class Counted(Stream inner, Tally tally) : ReadOnlyDecoderStream
    {
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            tally.Add(read);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// A decoder that writes its output (Bzip2Decoder) turned into a stream that is read: it runs on a thread of its own
/// and writes into a pipe of at most 1 MB, so memory stays bounded whatever the data unpacks to. Its errors come out of
/// <see cref="Read(Span{byte})"/>; disposing stops it and waits for it.
/// </summary>
internal sealed class ProducedStream : ReadOnlyDecoderStream
{
    private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 19, useSynchronizationContext: false));
    private readonly CancellationTokenSource _stop;
    private readonly Stream _output;
    private readonly Task _producer;

    public ProducedStream(Action<Stream, CancellationToken> produce, CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _output = _pipe.Reader.AsStream(leaveOpen: true);
        var token = _stop.Token;
        _producer = Task.Factory.StartNew(() =>
        {
            try
            {
                produce(new Input(_pipe.Writer, token), token);
                _pipe.Writer.Complete();
            }
            catch (Exception ex)
            {
                _pipe.Writer.Complete(ex);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public override int Read(Span<byte> buffer) => _output.Read(buffer);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stop.Cancel();
            _pipe.Reader.Complete();
            _producer.Wait(); // the producer catches everything
            _stop.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The producer's side: each write waits while the pipe is full and stops once the reader is gone.</summary>
    private sealed class Input(PipeWriter writer, CancellationToken token) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            writer.Write(buffer);
            if (writer.FlushAsync(token).AsTask().GetAwaiter().GetResult().IsCompleted)
                throw new OperationCanceledException(token);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
