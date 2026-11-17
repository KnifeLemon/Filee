// Memory is given back once conversions are over: after a quiet spell, never while one runs, and not again without
// a new reason.

using Filee.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

[Collection("Memory")]
public class MemoryTrimmerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(100);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Trims_once_after_a_quiet_spell_and_not_again_without_a_request()
    {
        using var trimmer = new MemoryTrimmer(() => false, () => { }, Delay);
        await Task.Delay(Delay * 3, TestContext.Current.CancellationToken);
        Assert.Equal(0, trimmer.Trims); // nothing happened yet

        trimmer.Request();
        trimmer.Request();
        await Until(() => trimmer.Trims > 0);
        await Task.Delay(Delay * 3, TestContext.Current.CancellationToken);
        Assert.Equal(1, trimmer.Trims);
    }

    [Fact]
    public async Task Never_trims_while_a_conversion_runs()
    {
        var busy = true;
        var trimmedWhileBusy = false;
        using var trimmer = new MemoryTrimmer(() => Volatile.Read(ref busy), () => trimmedWhileBusy |= Volatile.Read(ref busy), Delay);
        trimmer.Request();
        await Task.Delay(Delay * 5, TestContext.Current.CancellationToken);
        Assert.Equal(0, trimmer.Trims);

        Volatile.Write(ref busy, false);
        await Until(() => trimmer.Trims > 0);
        Assert.Equal(1, trimmer.Trims);
        Assert.False(trimmedWhileBusy);
    }

    [Fact]
    public void Trimming_decommits_the_managed_heap_that_was_freed()
    {
        // A heap grown by a large conversion and its garbage: without decommitting, the regions stay committed.
        var filled = new List<byte[]>();
        for (var i = 0; i < 300; i++)
            filled.Add(new byte[1024 * 1024]);
        filled.ForEach(block => Array.Fill(block, (byte)1));

        // The figures come from the latest collection. A young one refreshes them and frees none of this memory.
        GC.Collect(0);
        var peak = GC.GetGCMemoryInfo().TotalCommittedBytes;
        filled.Clear();

        MemoryTrimmer.Trim(NullLogger.Instance);

        // Read the same way: committed memory on both sides counts free space kept from earlier tests, which bytes in
        // use don't. Most of the 300 MB is gone, not all of it.
        GC.Collect(0);
        var after = GC.GetGCMemoryInfo().TotalCommittedBytes;
        Assert.True(after < peak - 150L * 1024 * 1024, $"{after} bytes stayed committed of {peak}");
    }

    [Fact]
    public async Task A_new_request_moves_the_trim_on()
    {
        using var trimmer = new MemoryTrimmer(() => false, () => { }, TimeSpan.FromSeconds(2));
        for (var i = 0; i < 5; i++)
        {
            trimmer.Request();
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.Equal(0, trimmer.Trims); // a conversion finishing every 100 ms: still converting
        await Until(() => trimmer.Trims > 0);
        Assert.Equal(1, trimmer.Trims);
    }
}

// Committed memory and forced collections belong to the whole process: a test collecting or allocating next to these
// would change the figures, and a collection already running can stand in for the one a trim asks for.
[CollectionDefinition("Memory", DisableParallelization = true)]
public sealed class MemoryCollection;
