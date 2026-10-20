// Generates the demo videos of Filee's features from its real donut, rendered headlessly frame by frame over a
// made-up desktop. Not a test: it only runs when FILEE_DEMO_VIDEOS names an output folder (build/make-demo-videos.ps1).
//
// Clips:  expand   a group slice fans out its ring (ExpandClip)
//         flip     Tab turns the donut over to its back face (FlipClip)
//         browser  a picture from a web page, with the download badge over the progress ring (BrowserClip)
//
// Per clip and theme:  <clip>-square.mp4 (1080×1080) and <clip>-vertical.mp4 (1080×1920) for social video,
//                      <clip>.webp (820 px, ≤ 1.5 MB), <clip>.gif (820 px, ≤ 3 MB) and <clip>-640.webp (≤ 400 KB)
//                      for pages and slides. The dark theme adds "-dark" after the clip's name.

using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests.DemoVideos;

[Collection("Motion")]
public class DemoVideoTests
{
    /// <summary>A clip: how to make it, whether it gets a dark version, and what the library needs for it meanwhile.</summary>
    private sealed record Clip(string Name, Func<DemoFormat, DemoPainter, RadialViewModel, DemoClip> Create, bool Dark, Func<IDisposable?> Setup);

    private static readonly Clip[] Clips =
    [
        new("expand", (format, painter, vm) => new ExpandClip(format, painter, vm), Dark: true, DemoData.Install),
        new("flip", (format, painter, vm) => new FlipClip(format, painter, vm), Dark: false, () => null),
        new("browser", (format, painter, vm) => new BrowserClip(format, painter, vm), Dark: false, () => null),
    ];

    [AvaloniaFact]
    public void Render_demo_videos()
    {
        var output = Environment.GetEnvironmentVariable("FILEE_DEMO_VIDEOS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(output), "Renders the demo videos only when FILEE_DEMO_VIDEOS is set (build/make-demo-videos.ps1).");
        var ffmpeg = DemoEncoder.Find(Repository());
        Assert.SkipWhen(ffmpeg is null, "FFmpeg not found: set FILEE_FFMPEG, run build/fetch-engines.ps1 or put it on the PATH.");
        var only = List("FILEE_DEMO_CLIPS");
        var stills = Environment.GetEnvironmentVariable("FILEE_DEMO_STILLS") is { Length: > 0 } s
            ? s.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray()
            : [];
        var quick = Environment.GetEnvironmentVariable("FILEE_DEMO_QUICK") == "1"; // stills only, no video
        var formats = List("FILEE_DEMO_FORMATS");
        var themes = List("FILEE_DEMO_THEMES");
        Directory.CreateDirectory(output!);

        TestServices.EnsureInitialized("en");
        var theme = AppHost.Get<ThemeService>();
        try
        {
            foreach (var clip in Clips.Where(c => only.Length == 0 || only.Contains(c.Name)))
            {
                using var data = clip.Setup();
                foreach (var dark in (clip.Dark ? new[] { false, true } : [false]).Where(d => themes.Length == 0 || themes.Contains(d ? "dark" : "light")))
                {
                    theme.Apply(new ThemeSettings { Mode = dark ? ThemeMode.Dark : ThemeMode.Light }, "en");
                    Motion.Set(true);
                    var name = dark ? $"{clip.Name}-dark" : clip.Name;
                    var palette = dark ? DemoPalette.Dark : DemoPalette.Light;
                    foreach (var format in new[] { DemoFormat.Square, DemoFormat.Vertical, DemoFormat.Wide }.Where(f => formats.Length == 0 || formats.Contains(f.Name)))
                    {
                        var file = Path.Combine(output!, format == DemoFormat.Wide ? $"{name}-master.mkv" : $"{name}-{format.Name}.mp4");
                        Record(clip, format, palette, ffmpeg!, quick ? null : file, stills,
                            Path.Combine(output!, "stills", $"{name}-{format.Name}"));
                    }
                    var master = Path.Combine(output!, $"{name}-master.mkv");
                    if (quick || !File.Exists(master))
                        continue;
                    DemoEncoder.WebP(ffmpeg!, master, Path.Combine(output!, $"{name}.webp"), 820, 1_500_000);
                    DemoEncoder.WebP(ffmpeg!, master, Path.Combine(output!, $"{name}-640.webp"), 640, 400_000);
                    DemoEncoder.Gif(ffmpeg!, master, Path.Combine(output!, $"{name}.gif"), 820, 3_000_000);
                    File.Delete(master);
                }
            }
        }
        finally
        {
            theme.Apply(new ThemeSettings(), "en");
        }
    }

    private static void Record(Clip clip, DemoFormat format, DemoPalette palette, string ffmpeg, string? file,
        IReadOnlyList<double> stills, string stillPrefix)
    {
        var vm = AppHost.Get<RadialViewModel>();
        // The font Filee draws the donut with (ThemeService), for the scene's text too.
        var font = Application.Current!.TryGetResource("AppFontFamily", null, out var value) && value is FontFamily f ? f : FontFamily.Default;
        var painter = new DemoPainter(palette, font);
        using var demo = clip.Create(format, painter, vm);
        using var frame = new RenderTargetBitmap(format.Pixels);
        using var sink = file is null ? null
            : new FrameSink(ffmpeg, format.Pixels, DemoEncoder.Fps, file.EndsWith(".mkv", StringComparison.Ordinal) ? DemoEncoder.Master(file) : DemoEncoder.Mp4(file));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var frames = (int)Math.Round(demo.Duration * DemoEncoder.Fps);
        var pending = stills.Select(s => (int)Math.Round(s * DemoEncoder.Fps)).ToHashSet();
        if (sink is null && pending.Count > 0)
            frames = Math.Min(frames, pending.Max() + 1);
        for (var i = 0; i < frames; i++)
        {
            using (var dc = frame.CreateDrawingContext(true))
                demo.Render(dc, i / (double)DemoEncoder.Fps);
            sink?.Write(frame);
            if (pending.Contains(i))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stillPrefix)!);
                frame.Save($"{stillPrefix}-{i / (double)DemoEncoder.Fps:00.00}s.png", new PngBitmapEncoderOptions());
            }
        }
        sink?.Finish();
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(stillPrefix))!, "render.log"),
            $"{Path.GetFileName(stillPrefix)}: {frames} frames in {watch.Elapsed.TotalSeconds:0.0} s{Environment.NewLine}");
    }

    private static string[] List(string variable) =>
        (Environment.GetEnvironmentVariable(variable) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Repository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
                return dir.FullName;
        return Directory.GetCurrentDirectory();
    }
}

/// <summary>
/// The donut the clips show: the image donut with a "Formats" group (PNG, JPG, WEBP) among seven presets, put in place
/// for the recording and taken away afterwards (in the tests' throw-away data folder).
/// </summary>
internal sealed class DemoData : IDisposable
{
    public const string GroupId = "demo-formats";

    private readonly UserDataStore _store = AppHost.Get<UserDataStore>();
    private readonly List<string> _before;
    private readonly Preset _group = new()
    {
        Id = GroupId,
        Name = "Formats",
        Kind = PresetKind.Group,
        SubPresetIds = ["to-png", "to-jpg", "to-webp"],
    };

    private DemoData()
    {
        var images = _store.Profiles.Single(p => p.Id == "images");
        _before = [.. images.PresetIds];
        _store.Presets.Add(_group);
        images.PresetIds.Clear();
        images.PresetIds.AddRange(["to-pdf", "trace-svg", GroupId, "to-tiff", "to-bmp", "to-ico", "half-size", "trace-line-art"]);
    }

    public static DemoData Install() => new();

    public void Dispose()
    {
        var images = _store.Profiles.Single(p => p.Id == "images");
        images.PresetIds.Clear();
        images.PresetIds.AddRange(_before);
        _store.Presets.Remove(_group);
    }
}
