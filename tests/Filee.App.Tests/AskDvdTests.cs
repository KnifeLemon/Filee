// "Choose the DVD title": the longest title and every track chosen to start with, a title picked by typing its number,
// the tracks unticked left out of the choice; when the window opens (video and audio presets only); and the window
// itself in light and dark.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Media;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class AskDvdTests
{
    /// <summary>A film (title 2) between a warning and a trailer.</summary>
    private static readonly DvdVideoIso.Title[] Titles =
    [
        new(1, 1, TimeSpan.FromSeconds(20), 1),
        new(2, 2, new TimeSpan(1, 4, 12), 16),
        new(3, 1, TimeSpan.FromMinutes(2), 1),
    ];

    /// <summary>The film has five languages; the trailer one track and no subtitles.</summary>
    private static Task<DvdVideoIso.Tracks> Tracks(int title, CancellationToken _) => Task.FromResult(title == 2
        ? new DvdVideoIso.Tracks(
            [new(0, "eng", "ac3", 6), new(1, "fre", "ac3", 6), new(2, "dut", "ac3", 6), new(3, "hun", "ac3", 2), new(4, "cze", "ac3", 2)],
            [new(6, "eng", "dvd_subtitle"), new(7, "fre", "dvd_subtitle"), new(8, "dut", "dvd_subtitle"), new(9, "hun", "dvd_subtitle"),
                new(10, null, "dvd_subtitle")])
        : new DvdVideoIso.Tracks([new(0, "eng", "ac3", 2)], []));

    private static AskDvdViewModel Window(string language = "en")
    {
        TestServices.EnsureInitialized(language);
        return new AskDvdViewModel(@"D:\DVD DISNEY ISO\4_DUMBO.ISO", Titles, Tracks, AppHost.Get<ILocalizer>());
    }

    [AvaloniaFact]
    public void The_longest_title_and_every_track_are_chosen_to_start_with()
    {
        using var vm = Window();

        Assert.Equal(2, vm.SelectedTitle!.Number);
        Assert.Equal("Title 2 · 1:04:12 · 16 chapters", vm.TitleText);
        Assert.Equal("3 titles on this disc", vm.SummaryText);
        Assert.True(vm.IsValid);
        Assert.Equal(["English · AC-3 5.1", "Français · AC-3 5.1", "Nederlands · AC-3 5.1", "Magyar · AC-3 2.0", "Čeština · AC-3 2.0"],
            vm.AudioTracks.Select(t => t.Label));
        Assert.Equal("Unknown language", vm.SubtitleTracks[^1].Label);
        var choice = vm.Result()!;
        Assert.Equal(2, choice.Title);
        Assert.Equal([0, 1, 2, 3, 4], choice.AudioTracks);
        Assert.Equal([6, 7, 8, 9, 10], choice.SubtitleTracks);
    }

    [AvaloniaFact]
    public void A_title_is_picked_by_typing_its_number()
    {
        using var vm = Window();

        vm.TitleText = "3";

        Assert.Equal(3, vm.SelectedTitle!.Number);
        Assert.Equal(["English · AC-3 2.0"], vm.AudioTracks.Select(t => t.Label));
        Assert.Empty(vm.SubtitleTracks);
        Assert.True(vm.ShowsNoSubtitles);
        Assert.Equal(3, vm.Result()!.Title);

        // A number the disc doesn't have: nothing to start, and the titles it has.
        vm.TitleText = "12";
        Assert.Null(vm.SelectedTitle);
        Assert.False(vm.IsValid);
        Assert.Null(vm.Result());
        Assert.Equal("This disc has titles 1 to 3", vm.Status);

        vm.TitleText = "title 1";
        Assert.Equal(1, vm.SelectedTitle!.Number);
        Assert.True(vm.IsValid);
    }

    [AvaloniaFact]
    public void Unticked_tracks_are_left_out()
    {
        using var vm = Window();

        foreach (var track in vm.AudioTracks.Where(t => t.Id != 1))
            track.Keep = false;
        vm.SubtitleTracks[0].Keep = false;

        var choice = vm.Result()!;
        Assert.Equal([1], choice.AudioTracks);
        Assert.Equal([7, 8, 9, 10], choice.SubtitleTracks);
    }

    [AvaloniaFact]
    public async Task Tracks_that_cant_be_read_say_so_and_start_nothing()
    {
        TestServices.EnsureInitialized("en");
        using var vm = new AskDvdViewModel("film.iso", Titles,
            (_, _) => Task.FromException<DvdVideoIso.Tracks>(new InvalidOperationException("FFmpeg cannot read this file.")),
            AppHost.Get<ILocalizer>());
        await Task.Yield();

        Assert.False(vm.IsReading);
        Assert.Equal("Filee couldn't read this title's tracks", vm.Status);
        Assert.Null(vm.Result());
    }

    [AvaloniaFact]
    public void Only_mkv_keeps_the_subtitles_and_an_audio_target_has_none()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        using var mp4 = new AskDvdViewModel("film.iso", Titles, Tracks, loc, "mp4");
        Assert.True(mp4.ShowsSubtitles);
        Assert.True(mp4.ShowsMkvHint);
        Assert.All(mp4.SubtitleTracks, t => Assert.False(t.Keep));
        Assert.Empty(mp4.Result()!.SubtitleTracks);

        using var mp3 = new AskDvdViewModel("film.iso", Titles, Tracks, loc, "mp3");
        Assert.False(mp3.ShowsSubtitles);
        Assert.False(mp3.ShowsMkvHint);

        using var mkv = new AskDvdViewModel("film.iso", Titles, Tracks, loc, "mkv");
        Assert.False(mkv.ShowsMkvHint);
        Assert.All(mkv.SubtitleTracks, t => Assert.True(t.Keep));
    }

    [Theory]
    [InlineData("fre", "Français")]
    [InlineData("fra", "Français")]
    [InlineData("ger", "Deutsch")]
    [InlineData("eng", "English")]
    [InlineData("und", null)]
    [InlineData(null, null)]
    public void Languages_are_named_in_their_own_language(string? code, string? name) =>
        Assert.Equal(name, AskDvdViewModel.LanguageName(code));

    [Fact]
    public void Only_video_and_audio_presets_ask_for_the_title()
    {
        var presets = BuiltInData.CreatePresets();
        Assert.True(AskedDvd.Asks(presets.Single(p => p.Id == "to-mkv")));
        Assert.True(AskedDvd.Asks(presets.Single(p => p.Id == "to-mp3")));
        Assert.False(AskedDvd.Asks(presets.Single(p => p.Id == "extract")));
        Assert.False(AskedDvd.Asks(presets.Single(p => p.Id == "to-zip")));
        Assert.False(AskedDvd.Asks(presets.Single(p => p.Id == "merge-videos")));
        // A retry converts the same title: it doesn't ask again.
        var chosen = AskedDvd.Apply(presets.Single(p => p.Id == "to-mkv"), new DvdChoice(2, [1], []));
        Assert.False(AskedDvd.Asks(chosen));
        Assert.Null(presets.Single(p => p.Id == "to-mkv").Media.Dvd);
        Assert.True(AskedDvd.IsDiscImage(@"D:\DVD\Film.ISO"));
        Assert.False(AskedDvd.IsDiscImage(@"D:\DVD\Film.mkv"));
    }

    public static TheoryData<string, bool> Looks => new()
    {
        { "en", false }, { "en", true }, { "fr", false }, { "fr", true }, { "ko", false }, { "de", true }, { "ar", false },
        { "ru", true }, { "ja", false }, { "hi", true },
    };

    /// <summary>An MP4 target: the subtitles unticked and the hint under them, in light and dark.</summary>
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("fr", true)]
    public void The_mp4_window_says_only_mkv_keeps_subtitles(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        theme.Apply(new ThemeSettings { Mode = dark ? ThemeMode.Dark : ThemeMode.Light }, language);
        var loc = AppHost.Get<ILocalizer>();
        using var vm = new AskDvdViewModel(@"D:\DVD DISNEY ISO\4_DUMBO.ISO", Titles, Tracks, loc, "mp4");
        var window = new AskDvdWindow { DataContext = vm };
        window.StartDvd.Content = loc.DisplayName(BuiltInData.CreatePresets().Single(p => p.Id == "to-mp4"));
        try
        {
            window.Show();
            RenderTests.Pump();
            var hint = window.FindControl<Control>("MkvHint")!;
            Assert.True(hint.IsEffectivelyVisible);
            var box = new Rect(hint.Bounds.Size).TransformToAABB(hint.TransformToVisual(window)!.Value);
            Assert.True(new Rect(window.Bounds.Size).Contains(box), $"{box} outside the window");
            RenderTests.Save(window, $"ask-dvd-mp4-{language}-{(dark ? "dark" : "light")}.png");
        }
        finally
        {
            window.Close();
            theme.Apply(new ThemeSettings(), language);
        }
    }

    /// <summary>The window in light and dark, long languages and right to left: everything inside it, the columns apart.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Looks))]
    public void The_window_fits_in_light_and_dark(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        theme.Apply(new ThemeSettings { Mode = dark ? ThemeMode.Dark : ThemeMode.Light }, language);
        var loc = AppHost.Get<ILocalizer>();
        var mkv = BuiltInData.CreatePresets().Single(p => p.Id == "to-mkv");
        using var vm = new AskDvdViewModel(@"D:\DVD DISNEY ISO\4_DUMBO.ISO", Titles, Tracks, loc, "mkv", 0, 2);
        var window = new AskDvdWindow { DataContext = vm };
        window.StartDvd.Content = loc.DisplayName(mkv);
        try
        {
            window.Show();
            RenderTests.Pump();
            Rect Box(string name)
            {
                var control = window.FindControl<Control>(name)!;
                return new Rect(control.Bounds.Size).TransformToAABB(control.TransformToVisual(window)!.Value);
            }
            var bounds = new Rect(window.Bounds.Size);
            foreach (var name in new[] { "FileName", "TitleCount", "TitleBox", "AudioList", "SubtitleList", "StartDvd", "CancelDvd" })
            {
                var box = Box(name);
                Assert.True(box.Width > 0 && bounds.Contains(box), $"{name} {box} outside {bounds}");
            }
            Assert.False(Box("AudioColumn").Deflate(1).Intersects(Box("SubtitleColumn").Deflate(1)));
            Assert.False(Box("StartDvd").Deflate(1).Intersects(Box("CancelDvd").Deflate(1)));
            if (language is "ar")
                Assert.True(Box("AudioColumn").Left > Box("SubtitleColumn").Left);
            // Dark: two tracks left out, as a user would.
            if (dark)
            {
                vm.AudioTracks[2].Keep = false;
                vm.SubtitleTracks[2].Keep = false;
                RenderTests.Pump();
            }
            RenderTests.Save(window, $"ask-dvd-{language}-{(dark ? "dark" : "light")}.png");
        }
        finally
        {
            window.Close();
            theme.Apply(new ThemeSettings(), language);
        }
    }
}
