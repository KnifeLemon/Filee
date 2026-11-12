// Pins first-start cleanup decisions independently of the real user's installed integration.

using Filee.App.Services;

namespace Filee.App.Tests;

public sealed class PortableCleanupTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("filee-cleanup-decision-").FullName;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cleanup_decision_is_remembered_across_launches(bool accept)
    {
        var prompts = 0;
        var removals = 0;
        for (var launch = 0; launch < 2; launch++)
            await PortableFirstStart.OfferCleanupAsync(_data, () => true, () => { removals++; return Task.CompletedTask; }, async remove =>
            {
                prompts++;
                if (accept) await remove();
                return accept;
            });
        Assert.Equal(1, prompts);
        Assert.Equal(accept ? 1 : 0, removals);
        Assert.Equal(accept ? "completed" : "declined", File.ReadAllText(Path.Combine(_data, PortableFirstStart.CleanupDecisionFile)));
    }

    [Fact]
    public async Task Failed_cleanup_does_not_record_success_and_can_be_retried()
    {
        await Assert.ThrowsAsync<IOException>(() => PortableFirstStart.OfferCleanupAsync(_data, () => true,
            () => throw new IOException("cleanup failed"), async remove => { await remove(); return true; }));
        Assert.False(File.Exists(Path.Combine(_data, PortableFirstStart.CleanupDecisionFile)));
        var retried = false;
        await PortableFirstStart.OfferCleanupAsync(_data, () => true, () => { retried = true; return Task.CompletedTask; },
            async remove => { await remove(); return true; });
        Assert.True(retried);
    }

    [Fact]
    public async Task No_owned_entries_does_not_prompt_or_record_a_decision()
    {
        await PortableFirstStart.OfferCleanupAsync(_data, () => false, () => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException());
        Assert.False(File.Exists(Path.Combine(_data, PortableFirstStart.CleanupDecisionFile)));
    }

    public void Dispose() => Directory.Delete(_data, recursive: true);
}
