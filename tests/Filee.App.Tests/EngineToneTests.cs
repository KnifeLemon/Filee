// The colour of an engine's status tag and dot on Settings → Engines, from why it can't run.

using Filee.App.ViewModels.Pages;
using Filee.Core.Conversion;

namespace Filee.App.Tests;

public class EngineToneTests
{
    [Fact]
    public void An_available_engine_is_green()
    {
        Assert.Equal(EngineTone.Ok, EngineItemViewModel.ToneOf(EngineStatus.Available()));
    }

    [Fact]
    public void A_missing_optional_engine_is_not_an_error()
    {
        Assert.Equal(EngineTone.Neutral, EngineItemViewModel.ToneOf(EngineStatus.Unavailable("engine.reason.not_installed")));
    }

    [Fact]
    public void A_failed_check_is_red()
    {
        Assert.Equal(EngineTone.Danger, EngineItemViewModel.ToneOf(EngineStatus.Unavailable("engine.reason.error")));
    }

    [Theory]
    [InlineData("engine.reason.unsupported_platform")]
    [InlineData("engine.reason.needs_package")]
    [InlineData("engine.reason.needs_perl")]
    [InlineData("engine.reason.download_translator")]
    public void Every_other_reason_it_cannot_run_here_is_amber(string reasonKey)
    {
        Assert.Equal(EngineTone.Warn, EngineItemViewModel.ToneOf(EngineStatus.Unavailable(reasonKey)));
    }
}
