// "filee presets check <folder>" (#1530): what the preset gallery's CI runs on a submission. Pictures are made with
// SkiaSharp in the test and decoded by the command in full, so a picture broken past its header fails too.

using System.Text.Json;
using Filee.Core.Presets;
using Filee.Core.Settings;
using SkiaSharp;

namespace Filee.Cli.Tests;

public sealed class PresetsCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-presets-check-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static async Task<(int Code, string Output, string Errors)> Run(params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        // 'presets check' reads no settings: a host would fail the test.
        var code = await Cli.RunAsync(args, output, errors, CancellationToken.None, () => throw new InvalidOperationException("no host"));
        return (code, output.ToString(), errors.ToString());
    }

    /// <summary>A shared preset's folder: <paramref name="preset"/> exported as Filee does, and the pictures given.</summary>
    private string Folder(Preset preset, params (string Name, byte[] Data)[] images)
    {
        var folder = Path.Combine(_root, "shared");
        Directory.CreateDirectory(Path.Combine(folder, "images"));
        var data = Path.Combine(_root, "data");
        var store = new UserDataStore(data);
        store.Load();
        store.Presets.Clear();
        store.Presets.Add(preset);
        store.Profiles.Clear();
        store.Export(Path.Combine(folder, "preset.json"));
        foreach (var (name, bytes) in images)
            File.WriteAllBytes(Path.Combine(folder, "images", name), bytes);
        return folder;
    }

    private static Preset Photo() => new()
    {
        Id = "insta",
        Name = "Instagram JPG",
        TargetFormat = "jpg",
        Image = { Quality = 85, Resize = ResizeMode.LongEdge, LongEdge = 1080 },
    };

    private static byte[] Picture(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
            using var paint = new SKPaint { Color = SKColors.Orange };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 90);
        return encoded.ToArray();
    }

    [Fact]
    public async Task A_shared_preset_that_follows_the_rules_passes()
    {
        var folder = Folder(Photo(), ("1.png", Picture(1280, 720, SKEncodedImageFormat.Png)),
            ("2.jpg", Picture(800, 800, SKEncodedImageFormat.Jpeg)), ("3.webp", Picture(720, 1280, SKEncodedImageFormat.Webp)));

        var result = await Run("presets", "check", folder);

        Assert.True(result.Code == 0, result.Output + result.Errors);
        Assert.Contains("OK: 1 preset(s), 0 donut(s)", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_gives_the_problems_with_codes_and_what_the_preset_is()
    {
        var preset = Photo();
        preset.Output.Location = OutputLocation.CustomFolder;
        preset.Output.CustomFolder = Path.Combine(_root, "out");
        var folder = Folder(preset, ("1.png", Picture(320, 200, SKEncodedImageFormat.Png)));

        var result = await Run("presets", "check", folder, "--json");

        Assert.Equal(1, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        var errors = root.GetProperty("errors").EnumerateArray().ToList();
        Assert.Contains(errors, e => e.GetProperty("code").GetString() == "field.left_out" && e.GetProperty("path").GetString() == "presets[0].output.customFolder");
        Assert.Contains(errors, e => e.GetProperty("code").GetString() == "images.size" && e.GetProperty("file").GetString() == "images/1.png");
        Assert.All(errors, e => Assert.False(string.IsNullOrEmpty(e.GetProperty("message").GetString())));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("derived").ValueKind); // preset.json didn't pass its JSON rules
    }

    [Fact]
    public async Task Json_of_a_passing_preset_has_what_meta_json_needs()
    {
        var folder = Folder(Photo(), ("1.webp", Picture(1600, 900, SKEncodedImageFormat.Webp)));

        var result = await Run("presets", "check", folder, "--json");

        Assert.True(result.Code == 0, result.Output);
        using var json = JsonDocument.Parse(result.Output);
        var derived = json.RootElement.GetProperty("derived");
        Assert.Equal(["convert"], derived.GetProperty("kinds").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["jpg"], derived.GetProperty("formats").GetProperty("to").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("2.5.0", derived.GetProperty("minVersion").GetString());
        Assert.Equal(64, derived.GetProperty("sha256").GetString()!.Length);
        Assert.Empty(json.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Empty(derived.GetProperty("languages").EnumerateArray()); // no meta.json
    }

    [Fact]
    public async Task Risky_settings_pass_with_warnings_for_the_gallery()
    {
        var preset = Photo();
        preset.Output.ReplaceOriginal = true;
        preset.Image.KeepMetadata = false;
        var folder = Folder(preset, ("1.png", Picture(1280, 720, SKEncodedImageFormat.Png)));

        var json = await Run("presets", "check", folder, "--json");
        var text = await Run("presets", "check", folder);

        Assert.True(json.Code == 0, json.Output);
        using var document = JsonDocument.Parse(json.Output);
        var warnings = document.RootElement.GetProperty("derived").GetProperty("warnings").EnumerateArray().ToList();
        Assert.Equal(["replaces_original", "strips_metadata"], warnings.Select(w => w.GetProperty("code").GetString()));
        Assert.Equal(["high", "info"], warnings.Select(w => w.GetProperty("severity").GetString()));
        Assert.Equal(["insta"], warnings[0].GetProperty("presets").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains("warning (high):", text.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_picture_broken_past_its_header_fails_decoding()
    {
        var png = Picture(1280, 720, SKEncodedImageFormat.Png);
        var broken = png[..(png.Length / 3)];
        var folder = Folder(Photo(), ("1.png", broken));

        var result = await Run("presets", "check", folder);

        Assert.Equal(1, result.Code);
        Assert.Contains("(images.decode)", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quiet_prints_nothing_when_it_passes()
    {
        var folder = Folder(Photo(), ("1.jpg", Picture(1000, 1000, SKEncodedImageFormat.Jpeg)));
        var result = await Run("presets", "check", folder, "--quiet");
        Assert.Equal(0, result.Code);
        Assert.Equal("", result.Output);
    }

    [Fact]
    public async Task A_missing_folder_or_wrong_arguments_are_usage_errors()
    {
        Assert.Equal(2, (await Run("presets", "check", Path.Combine(_root, "missing"))).Code);
        Assert.Equal(2, (await Run("presets", "check")).Code);
        Assert.Equal(2, (await Run("presets", "verify", _root)).Code);
        Assert.Equal(2, (await Run("presets", "check", _root, _root)).Code);
    }

    [Fact]
    public void Help_explains_the_check()
    {
        Assert.Contains("filee presets check <folder>", Help.Text(null), StringComparison.Ordinal);
        Assert.Contains("\"sha256\"", Help.Text("presets"), StringComparison.Ordinal);
    }
}
