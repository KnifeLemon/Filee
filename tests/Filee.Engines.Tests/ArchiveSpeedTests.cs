// Speed of the in-process archive readers next to the bundled 7-Zip on archives 7-Zip writes from generated files
// (text that compresses well and noise that doesn't, in a few hundred files). Not part of a normal run: set
// FILEE_ARCHIVE_BENCHMARK=1 (and optionally FILEE_ARCHIVE_BENCHMARK_MB, default 200). Prints a Markdown table.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Filee.Engines.Archives;
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
