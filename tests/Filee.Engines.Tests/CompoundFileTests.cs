// Compound file directories built in code that are legal in shape but hostile in size: storages nested in a chain far
// beyond what HWP and DOC use, and a directory with as many entries as the limits allow. Each must be refused or read
// quickly, in memory linear in the directory.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx.Doc;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

public class CompoundFileTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const uint None = 0xFFFFFFFF;

    [Fact]
    public void Storages_nested_to_the_limit_are_read_and_one_deeper_refused()
    {
        // The stream sits at the limit's depth: MaxStorageDepth - 1 storages above it.
        using (var file = Documents.CompoundFile.Open(new MemoryStream(Chain(Documents.CompoundFile.MaxStorageDepth - 1, "S"))))
        {
            var path = string.Join('/', Enumerable.Repeat("S", Documents.CompoundFile.MaxStorageDepth - 1)) + "/Leaf";
            Assert.Equal([path], file.Streams);
            Assert.Equal([], file.Read(path.ToLowerInvariant())!);
        }

        var refused = Assert.Throws<ConversionRefusedException>(() => Documents.CompoundFile.Open(new MemoryStream(Chain(Documents.CompoundFile.MaxStorageDepth, "S"))));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
    }

    [Fact]
    public void A_chain_of_twenty_thousand_storages_is_refused_quickly()
    {
        // 20,000 storages with 31-character names, each the only member of the last: every path would hold all its
        // ancestors' names (some 6e9 characters in all).
        var bytes = Chain(20_000, new string('x', 31));
        var watch = Stopwatch.StartNew();
        var refused = Assert.Throws<ConversionRefusedException>(() => Documents.CompoundFile.Open(new MemoryStream(bytes)));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Hwp_and_doc_files_with_deep_storages_are_refused_and_the_script_scan_gives_up()
    {
        var folder = fx.NewFolder();
        var hwp = Path.Combine(folder, "deep.hwp");
        var doc = Path.Combine(folder, "deep.doc");
        var bytes = Chain(20_000, new string('x', 31));
        File.WriteAllBytes(hwp, bytes);
        File.WriteAllBytes(doc, bytes);

        var watch = Stopwatch.StartNew();
        Assert.Equal(((string?)null, (string?)null), ComplexScripts.FindBothInFile(hwp, "hwp"));
        Assert.Equal(((string?)null, (string?)null), ComplexScripts.FindBothInFile(doc, "doc"));
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => DocFile.Open(doc, DocumentLimits.Default)).ErrorKey);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_directory_of_a_hundred_thousand_entries_is_looked_up_by_name_quickly()
    {
        // The root and 99,999 streams, all members of the root (a chain of right siblings): as many as MaxPackageEntries
        // allows. Every stream is looked up once, in other case.
        const int Count = 99_999;
        var entries = new List<(string, int, uint, uint, uint)> { ("Root Entry", 5, None, None, 1) };
        for (var i = 1; i <= Count; i++)
            entries.Add(($"Stream{i:D6}", 2, None, i < Count ? (uint)i + 1 : None, None));
        var bytes = Directory(entries);

        var watch = Stopwatch.StartNew();
        using var file = Documents.CompoundFile.Open(new MemoryStream(bytes));
        Assert.Equal(Count, file.Streams.Count());
        for (var i = 1; i <= Count; i++)
            Assert.Equal([], file.Read($"STREAM{i:D6}")!);
        Assert.False(file.Exists("Stream100000"));
        Assert.False(file.Exists("Stream000001/Stream000002"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");

        entries.Add(("One too many", 2, None, None, None));
        var refused = Assert.Throws<ConversionRefusedException>(() => Documents.CompoundFile.Open(new MemoryStream(Directory(entries))));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
    }

    [Fact]
    public void Paths_are_looked_up_storage_by_storage_and_without_case()
    {
        // Root → A (storage) → B (storage) → C (stream); D (stream) beside A; E (stream) beside C.
        var bytes = Directory([
            ("Root Entry", 5, None, None, 1),
            ("A", 1, None, 4, 2),
            ("B", 1, None, None, 3),
            ("C", 2, None, 5, None),
            ("D", 2, None, None, None),
            ("E", 2, None, None, None),
        ]);
        using var file = Documents.CompoundFile.Open(new MemoryStream(bytes));
        Assert.Equal(["A/B/C", "D", "A/B/E"], file.Streams);
        Assert.True(file.Exists("a/b"));
        Assert.True(file.Exists("A/b/c"));
        Assert.Null(file.Read("A/B")); // a storage, not a stream
        Assert.False(file.Exists("B/C"));
        Assert.False(file.Exists("A/B/C/"));
        Assert.False(file.Exists(""));
        Assert.Equal([], file.Read("a/b/e")!);
    }

    /// <summary>
    /// A compound file whose root holds <paramref name="depth"/> storages, each the only member of the last, and a stream
    /// "Leaf" in the innermost.
    /// </summary>
    private static byte[] Chain(int depth, string name)
    {
        var entries = new List<(string, int, uint, uint, uint)> { ("Root Entry", 5, None, None, 1) };
        for (var i = 1; i <= depth; i++)
            entries.Add((name, 1, None, None, (uint)i + 1));
        entries.Add(("Leaf", 2, None, None, None));
        return Directory(entries);
    }

    /// <summary>
    /// A version 4 compound file (4096-byte sectors) of only a directory: entries (name, type, left, right, child), the
    /// first the root. Streams are empty, so no sector holds data.
    /// </summary>
    private static byte[] Directory(IReadOnlyList<(string Name, int Type, uint Left, uint Right, uint Child)> entries)
    {
        const int Sector = 4096;
        const uint EndOfChain = 0xFFFFFFFE, FatSector = 0xFFFFFFFD;
        var directorySectors = (entries.Count * 128 + Sector - 1) / Sector;
        var fatSectors = 1;
        while (fatSectors * (Sector / 4) < fatSectors + directorySectors)
            fatSectors++;
        var file = new byte[(1L + fatSectors + directorySectors) * Sector];

        var head = file.AsSpan(0, 512);
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(head);
        BinaryPrimitives.WriteUInt16LittleEndian(head[24..], 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(head[26..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(head[28..], 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(head[30..], 12);
        BinaryPrimitives.WriteUInt16LittleEndian(head[32..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(head[40..], (uint)directorySectors);
        BinaryPrimitives.WriteUInt32LittleEndian(head[44..], (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(head[48..], (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(head[56..], 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(head[60..], EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(head[68..], EndOfChain);
        for (var i = 0; i < 109; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(head[(76 + i * 4)..], i < fatSectors ? (uint)i : None);

        var fat = file.AsSpan(Sector, fatSectors * Sector);
        for (var i = 0; i < fatSectors * Sector / 4; i++)
        {
            var value = i < fatSectors ? FatSector
                : i < fatSectors + directorySectors - 1 ? (uint)i + 1
                : i == fatSectors + directorySectors - 1 ? EndOfChain
                : None;
            BinaryPrimitives.WriteUInt32LittleEndian(fat[(i * 4)..], value);
        }

        var directory = file.AsSpan((1 + fatSectors) * Sector);
        for (var i = 0; i < entries.Count; i++)
        {
            var (name, type, left, right, child) = entries[i];
            var entry = directory.Slice(i * 128, 128);
            Encoding.Unicode.GetBytes(name).CopyTo(entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[64..], (ushort)((name.Length + 1) * 2));
            entry[66] = (byte)type;
            entry[67] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], left);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], right);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], child);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[116..], EndOfChain);
        }
        // Unused entries after the last: empty names, no type, no members.
        for (var i = entries.Count; i < directorySectors * Sector / 128; i++)
        {
            var entry = directory.Slice(i * 128, 128);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], None);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], None);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], None);
        }
        return file;
    }
}
