// What the idle trim reads and asks of Linux (issue #957): the parsing runs everywhere, the C library calls on Linux.

using System.Runtime.InteropServices;
using Filee.Platform.Linux;

namespace Filee.Platform.Tests;

public sealed class LinuxMemoryTests
{
    private const string Rollup = """
        55d0c1a00000-7ffd3f7ff000 ---p 00000000 00:00 0                          [rollup]
        Rss:              388424 kB
        Pss:              326550 kB
        Pss_Anon:         250000 kB
        Pss_File:          76550 kB
        Pss_Shmem:             0 kB
        Shared_Clean:      76140 kB
        Shared_Dirty:          0 kB
        Private_Clean:     12284 kB
        Private_Dirty:    300000 kB
        Referenced:       380000 kB
        Anonymous:        299000 kB
        LazyFree:              0 kB
        AnonHugePages:         0 kB
        Swap:                  0 kB
        Locked:                0 kB
        """;

    [Fact]
    public void Rollup_gives_resident_own_and_anonymous_memory()
    {
        var usage = ProcessMemoryUsage.Parse(Rollup);

        Assert.Equal(388424L * 1024, usage?.ResidentBytes);
        Assert.Equal(312284L * 1024, usage?.OwnBytes); // Private_Clean + Private_Dirty: the USS of smem
        Assert.Equal(299000L * 1024, usage?.AnonymousBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Private_Dirty: 12 kB")]
    [InlineData("Rss: many kB")]
    public void A_rollup_without_a_readable_rss_gives_nothing(string text) =>
        Assert.Null(ProcessMemoryUsage.Parse(text));

    [Fact]
    public void Off_Linux_nothing_is_asked_of_the_C_library()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux has these calls.");
            return;
        }
        Assert.False(NativeHeap.Trim());
        Assert.Null(NativeHeap.Usage());
        Assert.Null(ProcessMemoryUsage.Current());
    }

    [Fact]
    public void Trimming_gives_back_what_malloc_kept_on_linux()
    {
        if (!OperatingSystem.IsLinux() || NativeHeap.Usage() is null)
        {
            Assert.Skip("Requires glibc.");
            return;
        }
        Assert.NotNull(ProcessMemoryUsage.Current());

        // Small blocks stay in the arena when freed; the last one keeps the heap from simply shrinking at its end.
        var blocks = Enumerable.Range(0, 64 * 1024).Select(_ => Marshal.AllocHGlobal(1024)).ToArray();
        foreach (var block in blocks.SkipLast(1))
            Marshal.FreeHGlobal(block);
        var kept = NativeHeap.Usage()!.Value.FreeBytes;
        Assert.True(kept > 32 * 1024 * 1024, $"malloc kept {kept} bytes");

        Assert.True(NativeHeap.Trim());
        Assert.True(NativeHeap.Usage()!.Value.InUseBytes > 0);
        Marshal.FreeHGlobal(blocks[^1]);
    }
}
