// Speed of the in-process archive readers next to the bundled 7-Zip on archives 7-Zip writes from generated files
// (text that compresses well and noise that doesn't, in a few hundred files), of Filee's own 7z reader on a solid 7z
// of 100 files (next to 7-Zip and SharpCompress) and of its LZMA decoder alone, and size and speed of Filee's own 7z
// writer next to 7-Zip's. Not part of a normal run: set FILEE_ARCHIVE_BENCHMARK=1 (and optionally
// FILEE_ARCHIVE_BENCHMARK_MB, default 200 for the readers, 32 for the 7z reader and the decoder and 8 for the
// writer). Each prints a Markdown table.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using Filee.Engines.Archives.Lzma;
using Filee.Engines.Infrastructure;
using SharpCompress.Archives.SevenZip;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

[Trait("Category", "Benchmark")]
public class ArchiveSpeedTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    [Fact]
    public async Task In_process_readers_next_to_7zip()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK") == "1", "set FILEE_ARCHIVE_BENCHMARK=1");
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var megabytes = int.TryParse(Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK_MB"), out var mb) ? mb : 200;

        var dir = fx.NewFolder();
        var tree = Path.Combine(dir, "data");
        var random = new Random(1);
        var text = Words(1 << 20);
        long total = 0;
        for (var i = 0; total < megabytes * (1L << 20); i++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(tree, $"folder{i % 8}")).FullName;
            var size = random.Next(64 << 10, 2 << 20);
            byte[] bytes;
            if (i % 3 == 2)
            {
                bytes = Noise(size, i);
            }
            else
            {
                // Repeated text with a changed byte every 997, so it isn't one long match.
                bytes = new byte[size];
                for (var offset = 0; offset < size; offset += text.Length)
                    Array.Copy(text, 0, bytes, offset, Math.Min(text.Length, size - offset));
                for (var k = 0; k < size; k += 997)
                    bytes[k] = (byte)random.Next(32, 127);
            }
            await File.WriteAllBytesAsync(Path.Combine(folder, $"file{i}.{(i % 3 == 2 ? "bin" : "txt")}"), bytes, TestContext.Current.CancellationToken);
            total += size;
        }

        var tar = Path.Combine(dir, "data.tar");
        await PackAsync("a", "-ttar", tar, tree);
        var archives = new List<(string Label, string Path, string Format)>
        {
            ("ZIP (Deflate)", Path.Combine(dir, "data.zip"), "zip"),
            ("7z (LZMA2, solid)", Path.Combine(dir, "data.7z"), "7z"),
            ("TAR", tar, "tar"),
            ("TAR.GZ", Path.Combine(dir, "data.tar.gz"), "tgz"),
            ("TAR.BZ2", Path.Combine(dir, "data.tar.bz2"), "tbz2"),
            ("TAR.XZ", Path.Combine(dir, "data.tar.xz"), "txz"),
        };
        await PackAsync("a", "-tzip", "-mx5", archives[0].Path, tree);
        await PackAsync("a", "-t7z", "-mx5", archives[1].Path, tree);
        await PackAsync("a", "-tgzip", "-mx5", archives[3].Path, tar);
        await PackAsync("a", "-tbzip2", "-mx5", archives[4].Path, tar);
        await PackAsync("a", "-txz", "-mx5", archives[5].Path, tar);

        var table = new StringBuilder();
        table.AppendLine(CultureInfo.InvariantCulture, $"{total / (1 << 20)} MB in {Directory.GetFiles(tree, "*", SearchOption.AllDirectories).Length} files, {Environment.ProcessorCount} logical processors");
        table.AppendLine();
        table.AppendLine("| Archive | Size | Filee in-process | 7-Zip | In-process / 7-Zip |");
        table.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var (label, path, format) in archives)
        {
            var inProcess = await TimeAsync(dir, scratch => new ArchiveExtractor(null).ExtractAsync(path, format, scratch, CancellationToken.None));
            var external = await TimeAsync(dir, async scratch =>
            {
                // As the engine runs 7-Zip: list, unpack, and the same again for the TAR inside.
                var x0 = Path.Combine(scratch, "x0");
                await SevenZipAsync("l", "-slt", "-sccUTF-8", "-bsp0", "-pfilee-no-password", "--", path);
                await SevenZipAsync("x", "-y", "-aou", "-sccUTF-8", "-scsUTF-8", "-bsp0", "-bb0", "-pfilee-no-password", "-o" + x0, "--", path);
                var inner = Directory.GetFiles(x0);
                if (format != "tar" && inner.Length == 1 && inner[0].EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
                {
                    await SevenZipAsync("l", "-slt", "-sccUTF-8", "-bsp0", "-pfilee-no-password", "--", inner[0]);
                    await SevenZipAsync("x", "-y", "-aou", "-sccUTF-8", "-scsUTF-8", "-bsp0", "-bb0", "-pfilee-no-password", "-o" + Path.Combine(scratch, "x1"), "--", inner[0]);
                }
                return x0;
            });
            table.AppendLine(CultureInfo.InvariantCulture,
                $"| {label} | {new FileInfo(path).Length / (1 << 20)} MB | {inProcess.TotalSeconds:0.00} s | {external.TotalSeconds:0.00} s | {inProcess / external:0.00}× |");
            if (Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK_OUT") is { Length: > 0 } partial)
                await File.WriteAllTextAsync(partial, table.ToString(), TestContext.Current.CancellationToken);
        }
        TestContext.Current.TestOutputHelper?.WriteLine(table.ToString());
        if (Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK_OUT") is { Length: > 0 } outFile)
            await File.WriteAllTextAsync(outFile, table.ToString(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Seven_zip_writer_next_to_7zip()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK") == "1", "set FILEE_ARCHIVE_BENCHMARK=1");
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var megabytes = int.TryParse(Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK_MB"), out var mb) ? mb : 8;

        // Source-like text, prose, a CSV and some noise in a few dozen files.
        var dir = fx.NewFolder();
        var tree = Path.Combine(dir, "data");
        var random = new Random(2);
        long total = 0;
        for (var i = 0; total < megabytes * (1L << 20); i++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(tree, $"folder{i % 5}")).FullName;
            var size = random.Next(32 << 10, 768 << 10);
            var (bytes, extension) = (i % 4) switch
            {
                0 => (LzmaTests.Prose(size + i), "txt"),
                1 => (Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(i * 10_000, size / 24).Select(n => $"{n},{n % 97},{n * 31 % 1000}.5,값{n % 13}"))), "csv"),
                2 => (Words(size), "md"),
                _ => (Noise(size / 4, i), "bin"),
            };
            await File.WriteAllBytesAsync(Path.Combine(folder, $"file{i}.{extension}"), bytes, TestContext.Current.CancellationToken);
            total += bytes.Length;
        }

        var table = new StringBuilder();
        table.AppendLine(CultureInfo.InvariantCulture, $"{total / 1e6:0.0} MB in {Directory.GetFiles(tree, "*", SearchOption.AllDirectories).Length} files, 7-Zip {EngineVersions.Component("7zip")}, single-threaded (-mmt=off), CPU time");
        table.AppendLine();
        table.AppendLine("| Writer | Level | Size | Ratio | Pack (CPU) | Pack speed |");
        table.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var (level, sevenLevel) in new[] { (ArchiveLevel.Fast, "-mx1"), (ArchiveLevel.Normal, "-mx5"), (ArchiveLevel.Maximum, "-mx9") })
        {
            var ours = Path.Combine(dir, $"filee-{level}.7z");
            var cpu = ProcessCpu();
            await new ArchivePacker().PackAsync([new PackItem(tree, "data")], ours, "7z", level, dir, null, CancellationToken.None);
            Row("Filee", level.ToString(), new FileInfo(ours).Length, ProcessCpu() - cpu);
            await SevenZipAsync("t", ours);

            var theirs = Path.Combine(dir, $"7zip{sevenLevel}.7z");
            var time = await SevenZipCpuAsync("a", "-t7z", sevenLevel, "-mmt=off", theirs, tree);
            Row("7-Zip", sevenLevel, new FileInfo(theirs).Length, time);
        }
        TestContext.Current.TestOutputHelper?.WriteLine(table.ToString());
        if (Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK_OUT") is { Length: > 0 } outFile)
            await File.WriteAllTextAsync(outFile, table.ToString(), TestContext.Current.CancellationToken);

        void Row(string writer, string level, long size, TimeSpan cpu) =>
            table.AppendLine(CultureInfo.InvariantCulture,
                $"| {writer} | {level} | {size:N0} | {size * 100.0 / total:0.00}% | {cpu.TotalSeconds:0.00} s | {total / 1e6 / cpu.TotalSeconds:0.0} MB/s |");
    }

    [Fact]
    public async Task Seven_zip_reader_on_a_solid_archive_of_100_files()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK") == "1", "set FILEE_ARCHIVE_BENCHMARK=1");
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var megabytes = int.TryParse(Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK_MB"), out var mb) ? mb : 32;

        // 100 files: prose, CSV and some noise, about the same size each.
        var dir = fx.NewFolder();
        var tree = Path.Combine(dir, "data");
        var each = (int)(megabytes * (1L << 20) / 100);
        long total = 0;
        for (var i = 0; i < 100; i++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(tree, $"folder{i % 5}")).FullName;
            var bytes = (i % 5) switch
            {
                4 => Noise(each / 4, i),
                3 => Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(i * 100_000, each / 24).Select(n => $"{n},{n % 97},{n * 31 % 1000}.5,값{n % 13}"))),
                _ => LzmaTests.Prose(each + i),
            };
            await File.WriteAllBytesAsync(Path.Combine(folder, $"file{i:000}.txt"), bytes, TestContext.Current.CancellationToken);
            total += bytes.Length;
        }

        var table = new StringBuilder();
        table.AppendLine(CultureInfo.InvariantCulture, $"{total / 1e6:0.0} MB in 100 files, one solid block, 7-Zip {EngineVersions.Component("7zip")} single-threaded (-mmt=off), wall time, best of two");
        table.AppendLine();
        table.AppendLine("| Archive | Size | Filee | 7-Zip | SharpCompress | Filee / 7-Zip |");
        table.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var level in new[] { "-mx1", "-mx5", "-mx9" })
        {
            var archive = Path.Combine(dir, $"solid{level}.7z");
            await PackAsync("a", "-t7z", level, "-ms=on", archive, tree);
            var ours = await TimeAsync(dir, async scratch =>
            {
                await new ArchiveExtractor(null).ExtractAsync(archive, "7z", scratch, CancellationToken.None);
                return scratch;
            });
            var theirs = await TimeAsync(dir, async scratch =>
            {
                await SevenZipAsync("x", "-y", "-mmt=off", "-bsp0", "-bb0", "-o" + scratch, "--", archive);
                return scratch;
            });
            var library = await TimeAsync(dir, scratch =>
            {
                // What LibraryArchives did before: SharpCompress's solid reader, front to back.
                using var file = File.OpenRead(archive);
                using var opened = SevenZipArchive.Open(file, null);
                using var reader = opened.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                    if (!reader.Entry.IsDirectory)
                        using (var data = reader.OpenEntryStream())
                            data.CopyTo(Stream.Null);
                return Task.FromResult(scratch);
            });
            table.AppendLine(CultureInfo.InvariantCulture,
                $"| 7z {level} | {new FileInfo(archive).Length / 1e6:0.0} MB | {ours.TotalSeconds:0.00} s | {theirs.TotalSeconds:0.00} s | {library.TotalSeconds:0.00} s | {ours / theirs:0.00}× |");
        }
        TestContext.Current.TestOutputHelper?.WriteLine(table.ToString());
        if (Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK_OUT") is { Length: > 0 } outFile)
            await File.WriteAllTextAsync(outFile, table.ToString(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Lzma_decoder_next_to_7zip()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK") == "1", "set FILEE_ARCHIVE_BENCHMARK=1");
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var megabytes = int.TryParse(Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK_MB"), out var mb) ? mb : 32;
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "input.bin");
        byte[] data = [.. LzmaTests.Prose(megabytes << 19), .. Words(megabytes << 18), .. Noise(megabytes << 18, 3)];
        await File.WriteAllBytesAsync(source, data, TestContext.Current.CancellationToken);

        var table = new StringBuilder();
        table.AppendLine(CultureInfo.InvariantCulture, $"{data.Length / 1e6:0.0} MB (prose, words, noise) as .xz from 7-Zip {EngineVersions.Component("7zip")}, CPU time of decoding, best of three, single-threaded");
        table.AppendLine();
        table.AppendLine("| 7-Zip level | Size | Filee | 7-Zip | Filee / 7-Zip |");
        table.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var level in new[] { "-mx1", "-mx5", "-mx9" })
        {
            var xz = Path.Combine(dir, $"input{level}.xz");
            await PackAsync("a", "-txz", level, xz, source);
            var ours = TimeSpan.MaxValue;
            var theirs = TimeSpan.MaxValue;
            for (var run = 0; run < 3; run++)
            {
                var cpu = ProcessCpu();
                using (var file = File.OpenRead(xz))
                using (var reader = new XzReader(file, "input.xz"))
                    Assert.Equal(data.Length, ArchiveFiles.CopyLimited(reader, Stream.Null, long.MaxValue, CancellationToken.None));
                var time = ProcessCpu() - cpu;
                ours = time < ours ? time : ours;
                time = await SevenZipCpuAsync("t", "-mmt=off", xz);
                theirs = time < theirs ? time : theirs;
            }
            table.AppendLine(CultureInfo.InvariantCulture,
                $"| {level} | {new FileInfo(xz).Length / 1e6:0.0} MB | {data.Length / 1e6 / ours.TotalSeconds:0.0} MB/s | {data.Length / 1e6 / theirs.TotalSeconds:0.0} MB/s | {ours / theirs:0.00}× |");
        }
        TestContext.Current.TestOutputHelper?.WriteLine(table.ToString());
        if (Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK_OUT") is { Length: > 0 } outFile)
            await File.WriteAllTextAsync(outFile, table.ToString(), TestContext.Current.CancellationToken);
    }

    private static TimeSpan ProcessCpu()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    /// <summary>Runs 7-Zip and returns the CPU time it used.</summary>
    private static async Task<TimeSpan> SevenZipCpuAsync(params string[] args)
    {
        var start = new ProcessStartInfo(SevenZipExe!) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await output);
        return process.TotalProcessorTime;
    }

    private static Task SevenZipAsync(params string[] args) => Run7zAsync(TimeSpan.FromMinutes(30), args);

    /// <summary>Makes an input with 7-Zip, single-threaded (its multi-threaded LZMA2 stalled under the test host).</summary>
    private static Task PackAsync(params string[] args) => SevenZipAsync([args[0], "-mmt=off", "-y", .. args[1..]]);

    /// <summary>The best of two runs (after the archive was read once, so the file cache is warm for both).</summary>
    private static async Task<TimeSpan> TimeAsync(string dir, Func<string, Task<string>> run)
    {
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 2; i++)
        {
            var scratch = Directory.CreateDirectory(Path.Combine(dir, "run-" + Guid.NewGuid().ToString("N")[..6])).FullName;
            var stopwatch = Stopwatch.StartNew();
            await run(scratch);
            stopwatch.Stop();
            best = stopwatch.Elapsed < best ? stopwatch.Elapsed : best;
            ArchiveFiles.TryDeleteFolder(scratch);
        }
        return best;
    }
}
