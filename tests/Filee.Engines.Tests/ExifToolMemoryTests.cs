// An ExifTool read that fills the memory is stopped at the read's memory limit, 2 GB, long before its time limit.
// Off Windows the limit is a poll of the program's memory, so the stand-in has to really hold 2 GB first: alongside
// the image tests that hold gigabytes of their own, a 14 GB macOS Intel runner took 65 to 76 s to get there (alone,
// a few seconds). So these run on their own, after the rest.

using System.Diagnostics;
using Filee.Engines.Metadata;

namespace Filee.Engines.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FillsTheMemory
{
    public const string Name = "Fills the memory";
}

[Collection(FillsTheMemory.Name)]
public sealed class ExifToolMemoryTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-exiftool-memory-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task ExifTool_filling_the_memory_is_stopped()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "A shell script stands in for it.");
        if (OperatingSystem.IsWindows())
            return; // for the analyzer: SkipUnless throws
        var path = Path.Combine(_folder, "photo.jpg");
        File.WriteAllText(path, "x");
        var spy = Path.Combine(_folder, "exiftool");
        File.WriteAllText(spy, "#!/bin/sh\nexec perl -e 'my @held; push @held, q(a) x 10_000_000 for 1..300; sleep 300'\n");
        File.SetUnixFileMode(spy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<MetadataTooLargeException>(() => ExifTool.ReadAsync(spy, path, TestContext.Current.CancellationToken));

        // Half the read's 2 min time limit: stopped for its memory, not left to run out its time.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), clock.Elapsed.ToString());
    }
}
