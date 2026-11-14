// Filee's own 7z writer (Archives/SevenZipWriter): archives packed without 7-Zip read back by SharpCompress, by the
// in-process extractor and by the bundled 7-Zip (`7z t`, `7z x`, `7z l -slt`; skipped when it is missing): nested
// folders, Unicode names, empty files and folders, sizes and times, data across several solid blocks, the store
// level, an archive without entries, the LZMA-encoded header, the entry cap and cancellation.

using System.Diagnostics;
using System.Text;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using Filee.Engines.Archives.Lzma;
using SharpCompress.Archives.SevenZip;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class SevenZipWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime Older = new(2019, 12, 31, 23, 59, 58, 123, DateTimeKind.Utc);

    /// <summary>
    /// A tree with nested folders, Korean, Chinese and emoji names, an empty file, an empty folder, text and noise,
    /// and known times on a file and a folder.
    /// </summary>
    private static string MakeUnicodeTree(string parent)
    {
        var root = MakeTree(parent, "프로젝트 🚀");
        var deep = Directory.CreateDirectory(Path.Combine(root, "a", "b", "c 深い", "d")).FullName;
        File.WriteAllBytes(Path.Combine(deep, "깊은 파일.txt"), LzmaTests.Prose(120_000));
        File.WriteAllBytes(Path.Combine(root, "a", "빈 파일.txt"), []);
        File.WriteAllBytes(Path.Combine(root, "a", "b", "数据.csv"), Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"{i},{i * i},값{i}"))));
        File.WriteAllBytes(Path.Combine(root, "a", "b", "copy.txt"), LzmaTests.Prose(120_000)); // the same text again
        Directory.CreateDirectory(Path.Combine(root, "a", "비어 있음"));
        File.SetLastWriteTimeUtc(Path.Combine(deep, "깊은 파일.txt"), Older);
        Directory.SetLastWriteTimeUtc(deep, Older);
        return root;
    }

    private static async Task<string> PackAsync(string dir, IReadOnlyList<PackItem> items, ArchiveLevel level, string name = "out.7z")
    {
        var output = Path.Combine(dir, name);
        await new ArchivePacker(null).PackAsync(items, output, "7z", level, dir, null, Ct);
        return output;
    }

    // ───────── Round trips ─────────

    [Theory]
    [InlineData(ArchiveLevel.Store)]
    [InlineData(ArchiveLevel.Fast)]
    [InlineData(ArchiveLevel.Normal)]
    [InlineData(ArchiveLevel.Maximum)]
    public async Task SharpCompress_reads_names_folders_empty_entries_sizes_times_and_crcs(ArchiveLevel level)
    {
        Assert.Contains("7z", ArchivePacker.BuiltInFormats);
        var dir = fx.NewFolder();
        var tree = MakeUnicodeTree(dir);
        var output = await PackAsync(dir, [new PackItem(tree, "프로젝트 🚀")], level);

        await using var file = File.OpenRead(output);
        using var archive = SevenZipArchive.Open(file, null);
        var entries = archive.Entries.ToDictionary(e => e.Key!, StringComparer.Ordinal);
        Assert.False(archive.IsEncrypted);
        Assert.Equal(level != ArchiveLevel.Store, archive.IsSolid);

        foreach (var path in Directory.EnumerateFileSystemEntries(tree, "*", SearchOption.AllDirectories).Append(tree))
        {
            var key = "프로젝트 🚀" + (path == tree ? "" : "/" + Path.GetRelativePath(tree, path).Replace('\\', '/'));
            Assert.True(entries.TryGetValue(key, out var entry), key);
            if (Directory.Exists(path))
            {
                Assert.True(entry.IsDirectory, key);
                Assert.Equal(Directory.GetLastWriteTimeUtc(path), entry.LastModifiedTime!.Value.ToUniversalTime());
                continue;
            }
            Assert.False(entry.IsDirectory, key);
            Assert.Equal(new FileInfo(path).Length, entry.Size);
            Assert.Equal(File.GetLastWriteTimeUtc(path), entry.LastModifiedTime!.Value.ToUniversalTime());
            if (entry.Size > 0)
                Assert.Equal(Crc32.Compute(await File.ReadAllBytesAsync(path, Ct)), (uint)entry.Crc);
        }
        Assert.Equal(Directory.EnumerateFileSystemEntries(tree, "*", SearchOption.AllDirectories).Count() + 1, entries.Count);

        // The data itself, through SharpCompress's solid reader.
        using (var reader = archive.ExtractAllEntries())
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory || reader.Entry.Size == 0)
                    continue;
                await using var data = reader.OpenEntryStream();
                await using var copy = new MemoryStream();
                await data.CopyToAsync(copy, Ct);
                Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(dir, reader.Entry.Key!), Ct), copy.ToArray());
            }

        // And through the engine's in-process extractor (SharpCompress without 7-Zip), times included.
        var target = Directory.CreateDirectory(Path.Combine(dir, "unpacked")).FullName;
        var unpacked = await new ArchiveExtractor(null).ExtractAsync(output, "7z", target, Ct);
        var root = Path.Combine(unpacked, "프로젝트 🚀");
        Assert.Equal(Snapshot(tree), Snapshot(root));
        Assert.Equal(KnownTime, File.GetLastWriteTime(Path.Combine(root, "readme.txt")));
        Assert.Equal(Older, File.GetLastWriteTimeUtc(Path.Combine(root, "a", "b", "c 深い", "d", "깊은 파일.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(root, "a", "빈 파일.txt")).Length);
    }

    [Theory]
    [InlineData(ArchiveLevel.Store)]
    [InlineData(ArchiveLevel.Normal)]
    public async Task SevenZip_tests_lists_and_extracts_our_archives(ArchiveLevel level)
    {
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var dir = fx.NewFolder();
        var tree = MakeUnicodeTree(dir);
        var single = Path.Combine(dir, "단독.bin");
        await File.WriteAllBytesAsync(single, Noise(50_000, 3), Ct);
        var output = await PackAsync(dir, [new PackItem(tree, "프로젝트 🚀"), new PackItem(single, "단독.bin")], level);

        await Run7zAsync("t", output);
        var listing = (await SevenZipOutputAsync("l", "-slt", "-sccUTF-8", output)).Replace('\\', '/'); // Windows lists "a\b"
        Assert.Contains("Type = 7z", listing);
        Assert.Contains(level == ArchiveLevel.Store ? "Method = Copy" : "Method = LZMA2:", listing);
        Assert.Contains($"Solid = {(level == ArchiveLevel.Store ? "-" : "+")}", listing);
        Assert.Contains("Path = 프로젝트 🚀/a/b/c 深い/d/깊은 파일.txt", listing);
        Assert.Contains("Path = 프로젝트 🚀/a/비어 있음", listing);

        var target = Path.Combine(dir, "x");
        await Run7zAsync("x", "-y", "-o" + target, output);
        Assert.Equal(Snapshot(tree), Snapshot(Path.Combine(target, "프로젝트 🚀")));
        Assert.Equal(await File.ReadAllBytesAsync(single, Ct), await File.ReadAllBytesAsync(Path.Combine(target, "단독.bin"), Ct));
        Assert.Equal(KnownTime, File.GetLastWriteTime(Path.Combine(target, "프로젝트 🚀", "readme.txt")));
        Assert.Equal(Older, File.GetLastWriteTimeUtc(Path.Combine(target, "프로젝트 🚀", "a", "b", "c 深い", "d", "깊은 파일.txt")));
        Assert.Equal(Older, Directory.GetLastWriteTimeUtc(Path.Combine(target, "프로젝트 🚀", "a", "b", "c 深い", "d")));

        // The engine hands a solid 7z to 7-Zip when it is there: the same tree comes out.
        var engine = Directory.CreateDirectory(Path.Combine(dir, "engine")).FullName;
        var unpacked = await new ArchiveExtractor(SevenZip.Locate()).ExtractAsync(output, "7z", engine, Ct);
        Assert.Equal(Snapshot(tree), Snapshot(Path.Combine(unpacked, "프로젝트 🚀")));
    }

    [Fact]
    public async Task Large_compressible_data_spans_several_solid_blocks()
    {
        var dir = fx.NewFolder();
        var files = new List<(string Name, byte[] Data)>();
        for (var i = 0; i < 12; i++)
            files.Add(($"part {i:00}/{(i % 3 == 0 ? "data.bin" : "text.txt")}", i % 3 == 0 ? Noise(150_000, i) : LzmaTests.Prose(400_000 + i)));
        files.Add(("big/one file.txt", LzmaTests.Prose(3_000_000))); // larger than a block on its own
        var output = Path.Combine(dir, "blocks.7z");
        int blocks;
        await using (var stream = File.Create(output))
        {
            var writer = new SevenZipWriter(stream, ArchiveLevel.Fast, files.Sum(f => (long)f.Data.Length), Ct, blockLimit: 1 << 20);
            foreach (var (name, data) in files)
                writer.AddFile(name, new MemoryStream(data), data.Length, Older, 0x20);
            blocks = writer.BlockCount;
            writer.Finish();
        }
        Assert.True(blocks >= 5, $"{blocks} blocks");
        var total = files.Sum(f => f.Data.Length);
        Assert.True(new FileInfo(output).Length < total / 2, "text compresses");

        await using (var file = File.OpenRead(output))
        using (var archive = SevenZipArchive.Open(file, null))
        using (var reader = archive.ExtractAllEntries())
        {
            var seen = 0;
            while (reader.MoveToNextEntry())
            {
                var expected = files.Single(f => f.Name == reader.Entry.Key).Data;
                await using var data = reader.OpenEntryStream();
                await using var copy = new MemoryStream();
                await data.CopyToAsync(copy, Ct);
                Assert.Equal(expected, copy.ToArray());
                seen++;
            }
            Assert.Equal(files.Count, seen);
        }

        if (SevenZipExe is not null)
        {
            await Run7zAsync("t", output);
            Assert.Contains($"Blocks = {blocks}", await SevenZipOutputAsync("l", "-slt", output));
        }
    }

    [Fact]
    public async Task Only_folders_and_empty_files_and_no_entries_at_all()
    {
        var dir = fx.NewFolder();
        var empty = Directory.CreateDirectory(Path.Combine(dir, "빈 폴더", "안쪽")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(empty, "zero.txt"), [], Ct);
        var output = await PackAsync(dir, [new PackItem(Path.Combine(dir, "빈 폴더"), "빈 폴더")], ArchiveLevel.Normal);

        await using (var file = File.OpenRead(output))
        using (var archive = SevenZipArchive.Open(file, null))
        {
            Assert.Equal(["빈 폴더", "빈 폴더/안쪽", "빈 폴더/안쪽/zero.txt"], archive.Entries.Select(e => e.Key).Order(StringComparer.Ordinal));
            Assert.Equal(2, archive.Entries.Count(e => e.IsDirectory));
        }
        // (The engine's in-process reader refuses this one, from 7-Zip just the same: SharpCompress calls a 7z whose
        // only files are empty encrypted. Its reading is not this writer's concern.)

        var nothing = Path.Combine(dir, "nothing.7z");
        await using (var stream = File.Create(nothing))
            new SevenZipWriter(stream, ArchiveLevel.Normal, 0, Ct).Finish();
        Assert.Equal(32, new FileInfo(nothing).Length);
        await using (var file = File.OpenRead(nothing))
        using (var archive = SevenZipArchive.Open(file, null))
            Assert.Empty(archive.Entries);

        if (SevenZipExe is not null)
        {
            await Run7zAsync("t", output);
            await Run7zAsync("t", nothing);
        }
    }

    [Fact]
    public void The_header_is_lzma_compressed_when_that_is_smaller()
    {
        var text = LzmaTests.Prose(200_000);
        var packed = SevenZipWriter.CompressLzma(text, out var dictionary);
        Assert.NotNull(packed);
        Assert.True(packed.Length < text.Length / 2);
        using var reader = new LzmaRawReader(new MemoryStream(packed), LzmaProperties.Default, (uint)dictionary, text.Length);
        using var copy = new MemoryStream();
        reader.CopyTo(copy);
        Assert.Equal(text, copy.ToArray());

        Assert.Null(SevenZipWriter.CompressLzma(Noise(10_000, 1), out _)); // stays plain
    }

    [Fact]
    public async Task Archives_with_more_entries_than_the_cap_are_not_written()
    {
        var dir = fx.NewFolder();
        var folder = Directory.CreateDirectory(Path.Combine(dir, "many")).FullName;
        for (var i = 0; i < 20; i++)
            await File.WriteAllTextAsync(Path.Combine(folder, $"{i}.txt"), "x", Ct);
        ArchiveSafety.TestLimits.Value = (1 << 20, 100, 10);
        try
        {
            var output = Path.Combine(dir, "many.7z");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ArchivePacker(null).PackAsync([new PackItem(folder, "many")], output, "7z", ArchiveLevel.Normal, dir, null, Ct));
            Assert.Contains("10", error.Message);
            Assert.False(File.Exists(output));
        }
        finally
        {
            ArchiveSafety.TestLimits.Value = null;
        }
    }

    [Fact]
    public async Task A_cancelled_pack_leaves_nothing_behind()
    {
        var dir = fx.NewFolder();
        var source = Directory.CreateDirectory(Path.Combine(dir, "src")).FullName;
        for (var i = 0; i < 4; i++)
            await File.WriteAllBytesAsync(Path.Combine(source, $"part{i}.txt"), LzmaTests.Prose(2_000_000 + i), Ct);
        var output = Path.Combine(dir, "src.7z");
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p > 0.1) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ArchivePacker(null).PackAsync([new PackItem(source, "src")], output, "7z", ArchiveLevel.Normal, dir, progress, cancel.Token));
        Assert.False(File.Exists(output));
        Assert.Equal(["src"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>Runs 7-Zip and returns what it printed (UTF-8).</summary>
    private static async Task<string> SevenZipOutputAsync(params string[] args)
    {
        var start = new ProcessStartInfo(SevenZipExe!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, output);
        return output;
    }
}
