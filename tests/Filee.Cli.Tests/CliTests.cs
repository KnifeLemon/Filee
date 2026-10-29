// End-to-end tests of the "filee" command: real engines, a temporary settings folder, files in a temporary folder.

using System.Buffers.Binary;
using System.Text.Json;
using System.Xml.Linq;
using Filee.Core.Presets;
using Filee.Engines.Tests;

namespace Filee.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-cli-").FullName;
    private readonly string _settings = Directory.CreateTempSubdirectory("filee-cli-settings-").FullName;

    public void Dispose()
    {
        foreach (var dir in new[] { _folder, _settings })
            try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private string Png(string name)
    {
        var path = Path.Combine(_folder, name);
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Orange, 40, 30);
        image.Write(path, ImageMagick.MagickFormat.Png);
        return path;
    }

    private async Task<(int Code, string Output, string Errors)> Run(params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await Cli.RunAsync(args, output, errors, TestContext.Current.CancellationToken, () => CliHost.Create(_settings));
        return (code, output.ToString(), errors.ToString());
    }

    private static readonly ushort[] FitCrcTable =
        [0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401, 0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400];

    private static ushort FitCrc(IEnumerable<byte> bytes)
    {
        ushort crc = 0;
        foreach (var b in bytes)
        {
            crc = (ushort)((crc >> 4) ^ FitCrcTable[crc & 0xF] ^ FitCrcTable[b & 0xF]);
            crc = (ushort)((crc >> 4) ^ FitCrcTable[crc & 0xF] ^ FitCrcTable[b >> 4]);
        }
        return crc;
    }

    /// <summary>A FIT activity built from the protocol: a 14-byte header, an activity file_id, one record
    /// definition (timestamp, latitude, longitude) with a message per point, and the header and file CRCs.</summary>
    private static byte[] FitActivity(params (uint Time, int Lat, int Lon)[] points)
    {
        var data = new List<byte> { 0x40, 0, 0, 0, 0, 1, 0, 1, 0, 0x00, 4 };
        data.AddRange([0x41, 0, 0, 20, 0, 3, 253, 4, 0x86, 0, 4, 0x85, 1, 4, 0x85]);
        var value = new byte[4];
        foreach (var (time, lat, lon) in points)
        {
            data.Add(0x01);
            BinaryPrimitives.WriteUInt32LittleEndian(value, time);
            data.AddRange(value);
            BinaryPrimitives.WriteInt32LittleEndian(value, lat);
            data.AddRange(value);
            BinaryPrimitives.WriteInt32LittleEndian(value, lon);
            data.AddRange(value);
        }
        var file = new List<byte> { 14, 0x10, 0x34, 0x08 };
        BinaryPrimitives.WriteInt32LittleEndian(value, data.Count);
        file.AddRange(value);
        file.AddRange(".FIT"u8.ToArray());
        var headerCrc = FitCrc(file);
        file.AddRange([(byte)headerCrc, (byte)(headerCrc >> 8)]);
        file.AddRange(data);
        var fileCrc = FitCrc(file);
        file.AddRange([(byte)fileCrc, (byte)(fileCrc >> 8)]);
        return [.. file];
    }

    [Fact]
    public async Task Fit_activity_converts_to_gpx_with_point_times_using_the_builtin_preset()
    {
        var fit = Path.Combine(_folder, "ride.fit");
        File.WriteAllBytes(fit, FitActivity((0x1000003B, 0x20000000, 0x40000000), (0x1000003C, 0x30000000, 0x30000000),
            (0x1000003D, 0x40000000, 0x20000000)));

        var (code, output, errors) = await Run("convert", fit, "--preset", "to-gpx");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Contains("Converted 1 of 1", output);
        var gpx = XDocument.Load(Path.Combine(_folder, "ride.gpx"));
        var points = gpx.Descendants().Where(element => element.Name.LocalName == "trkpt").ToArray();
        Assert.Equal(3, points.Length);
        Assert.All(points, point => Assert.Single(point.Elements(), element => element.Name.LocalName == "time"));
    }

    [Fact]
    public async Task Converts_files_and_wildcards_into_an_output_folder()
    {
        Png("a.png");
        Png("b.png");
        var outFolder = Path.Combine(_folder, "out");

        var (code, output, errors) = await Run("convert", Path.Combine(_folder, "*.png"), "--to", "jpg", "-o", outFolder, "--quality", "70");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Contains("Converted 2 of 2", output);
        Assert.True(File.Exists(Path.Combine(outFolder, "a.jpg")));
        Assert.True(File.Exists(Path.Combine(outFolder, "b.jpg")));
    }

    [Fact]
    public async Task Zip_saves_the_result_of_each_file_as_one_zip()
    {
        var geojson = Path.Combine(_folder, "places.geojson");
        File.WriteAllText(geojson, """{"type":"Feature","properties":{"n":1},"geometry":{"type":"Point","coordinates":[1,2]}}""");

        var (code, _, _) = await Run("convert", geojson, "--to", "shp", "--zip");

        Assert.Equal(Cli.Success, code);
        using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(_folder, "places.zip"));
        Assert.Equal(["places.cpg", "places.dbf", "places.prj", "places.shp", "places.shx"], zip.Entries.Select(e => e.Name).Order().ToArray());
        Assert.False(File.Exists(Path.Combine(_folder, "places.shp")));
    }

    [Fact]
    public async Task Keep_dates_copies_the_dates_of_the_originals()
    {
        var source = Png("old.png");
        var modified = new DateTime(2018, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);

        var (code, _, _) = await Run("convert", source, "--to", "jpg", "--keep-dates");

        Assert.Equal(Cli.Success, code);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(_folder, "old.jpg")));
    }

    [Fact]
    public async Task Json_output_lists_every_file()
    {
        var source = Png("a.png");

        var (code, output, _) = await Run("convert", source, "--to", ".webp", "--json");

        Assert.Equal(Cli.Success, code);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(1, json.RootElement.GetProperty("converted").GetInt32());
        var file = json.RootElement.GetProperty("files")[0];
        Assert.Equal("done", file.GetProperty("state").GetString());
        Assert.EndsWith("a.webp", file.GetProperty("outputs")[0].GetString());
    }

    [Fact]
    public async Task Compress_pdf_says_when_a_pdf_cant_get_smaller()
    {
        var source = Path.Combine(_folder, "scan.pdf");
        using (var image = new ImageMagick.MagickImage("gradient:#C04020-#2060C0", 1800, 1800))
        {
            image.AddNoise(ImageMagick.NoiseType.Gaussian, 0.6);
            image.Density = new ImageMagick.Density(600);
            image.Write(source, ImageMagick.MagickFormat.Pdf);
        }

        var (code, output, errors) = await Run("convert", source, "--preset", "compress-pdf", "--json");
        Assert.Equal(Cli.Success, code);
        var compressed = Path.Combine(_folder, "scan_compressed.pdf");
        Assert.True(new FileInfo(compressed).Length < new FileInfo(source).Length / 2, errors);
        using (var json = JsonDocument.Parse(output))
            Assert.False(json.RootElement.GetProperty("files")[0].TryGetProperty("notice", out _));

        // Compressed already: written unchanged, with a note.
        (code, output, _) = await Run("convert", compressed, "--preset", "compress-pdf");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("scan_compressed_compressed.pdf (Couldn't make it smaller", output);
    }

    [Fact]
    public async Task A_folder_converts_the_files_filee_knows()
    {
        Png("a.png");
        File.WriteAllText(Path.Combine(_folder, "notes.unknownformat"), "x");

        var (code, output, _) = await Run("convert", _folder, "--preset", BuiltInData.CreatePresets().First(p => p.TargetFormat == "jpg").Id);

        Assert.Equal(Cli.Success, code);
        Assert.Contains("Converted 1 of 1", output);
    }

    [Fact]
    public async Task A_file_that_cannot_be_converted_fails_with_exit_code_1()
    {
        var text = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(text, "hello");

        var (code, output, _) = await Run("convert", text, "--to", "mp3");

        Assert.Equal(Cli.SomeFailed, code);
        Assert.Contains("fail", output);
    }

    [Fact]
    public async Task An_empty_file_says_it_is_empty()
    {
        var empty = Path.Combine(_folder, "new.docx");
        File.WriteAllBytes(empty, []);

        var (code, output, _) = await Run("convert", empty, "--to", "pdf");

        Assert.Equal(Cli.SomeFailed, code);
        Assert.Contains("new.docx: The file is empty (0 bytes).", output);
    }

    [Theory]
    [InlineData(new[] { "convert", "a.png" }, "--to")]
    [InlineData(new[] { "convert", "a.png", "--to", "jpg", "--preset", "x" }, "--to")]
    [InlineData(new[] { "convert", "a.png", "--to", "nosuchformat" }, "unknown format")]
    [InlineData(new[] { "convert", "a.png", "--preset", "nosuchpreset" }, "no preset")]
    [InlineData(new[] { "convert", "a.png", "--preset", "rotate" }, "rotate-left, rotate-right, rotate-180")]
    [InlineData(new[] { "convert", "--to", "jpg" }, "at least one file")]
    [InlineData(new[] { "convert", "a.png", "--to", "jpg", "--quality", "300" }, "--quality")]
    [InlineData(new[] { "frobnicate" }, "Unknown command")]
    public async Task Wrong_arguments_explain_themselves_with_exit_code_2(string[] args, string message)
    {
        Png("a.png");
        var withPaths = args.Select(a => a == "a.png" ? Path.Combine(_folder, a) : a).ToArray();

        var (code, _, errors) = await Run(withPaths);

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains(message, errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_input_is_reported()
    {
        var (code, _, errors) = await Run("convert", Path.Combine(_folder, "missing.png"), "--to", "jpg");

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("not found", errors);
    }

    [Fact]
    public async Task Watch_needs_an_existing_folder()
    {
        var (code, _, errors) = await Run("watch", Path.Combine(_folder, "nope"), "--to", "jpg");

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("not found", errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Watch_converts_files_that_arrive_until_cancelled()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = new StringWriter();
        var watching = Cli.RunAsync(["watch", _folder, "--to", "jpg", "--settle", "0.2"], TextWriter.Synchronized(output), TextWriter.Null,
            stop.Token, () => CliHost.Create(_settings));

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Png("arrived.png");
        var converted = Path.Combine(_folder, "converted", "arrived.jpg");
        for (var i = 0; i < 100 && !File.Exists(converted); i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        stop.Cancel();

        Assert.Equal(Cli.Success, await watching);
        Assert.True(File.Exists(converted), output.ToString());
    }

    [Fact]
    public async Task A_shared_watch_folder_does_not_reconvert_its_outputs_after_restarting_the_command()
    {
        var args = new[] { "watch", _folder, "--to", "jpg", "-o", _folder, "--move-originals", "--settle", "0.1" };
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            var watching = Cli.RunAsync(args, TextWriter.Null, TextWriter.Null, stop.Token, () => CliHost.Create(_settings));
            var input = Path.Combine(_folder, "arrived.webp");
            using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Orange, 40, 30))
                image.Write(input, ImageMagick.MagickFormat.WebP);
            var output = Path.Combine(_folder, "arrived.jpg");
            for (var i = 0; i < 100 && !File.Exists(Path.Combine(_folder, "originals", "arrived.webp")); i++)
                await Task.Delay(100, TestContext.Current.CancellationToken);
            stop.Cancel();
            Assert.Equal(Cli.Success, await watching);
            Assert.True(File.Exists(output));
            Assert.True(File.Exists(Path.Combine(_folder, "originals", "arrived.webp")));
            using var decoded = new ImageMagick.MagickImage(output);
            Assert.Equal(ImageMagick.MagickFormat.Jpeg, decoded.Format);
        }

        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            var watching = Cli.RunAsync(args, TextWriter.Null, TextWriter.Null, stop.Token, () => CliHost.Create(_settings));
            // Let the initial inbox scan settle and the one-second conversion timer run twice.
            await Task.Delay(2200, TestContext.Current.CancellationToken);
            stop.Cancel();
            Assert.Equal(Cli.Success, await watching);
            Assert.Equal(["arrived.jpg"], Directory.EnumerateFiles(_folder, "*.jpg").Select(Path.GetFileName));
            Assert.False(File.Exists(Path.Combine(_folder, "originals", "arrived.jpg")));
        }
    }

    [Fact]
    public async Task Formats_and_presets_are_listed()
    {
        var (code, output, _) = await Run("formats", "heic");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("jpg", output);

        (code, output, _) = await Run("presets");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("to-png", output);
    }

    [Fact]
    public async Task Archives_list_folder_as_a_target()
    {
        var (code, output, _) = await Run("formats", "zip");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("folder", output);

        (code, output, _) = await Run("formats", "png");
        Assert.Equal(Cli.Success, code);
        Assert.DoesNotContain("folder", output);
    }

    [Fact]
    public async Task Formats_json_lists_every_format()
    {
        var (code, output, _) = await Run("formats", "--json");
        Assert.Equal(Cli.Success, code);

        using var json = JsonDocument.Parse(output);
        var formats = json.RootElement.GetProperty("formats").EnumerateArray().ToList();
        var png = Assert.Single(formats, f => f.GetProperty("id").GetString() == "png");
        Assert.Equal("image", png.GetProperty("category").GetString());
        Assert.Contains("png", png.GetProperty("extensions").EnumerateArray().Select(e => e.GetString()));
        Assert.True(png.GetProperty("writable").GetBoolean());
        Assert.DoesNotContain(formats, f => f.GetProperty("id").GetString() == "folder");
    }

    [Fact]
    public async Task Formats_json_of_one_format_matches_the_text()
    {
        var (code, text, _) = await Run("formats", "zip");
        Assert.Equal(Cli.Success, code);
        (code, var output, _) = await Run("formats", "zip", "--json");
        Assert.Equal(Cli.Success, code);

        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal("zip", root.GetProperty("id").GetString());
        Assert.Equal("archive", root.GetProperty("category").GetString());
        var targets = root.GetProperty("targets").EnumerateArray().Select(e => e.GetString()!).ToList();
        var withEngines = root.GetProperty("withEngines").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains("folder", targets.Concat(withEngines));
        Assert.Empty(targets.Intersect(withEngines));
        foreach (var id in targets.Concat(withEngines))
            Assert.Contains(id, text);
    }

    [Fact]
    public async Task Jpeg_xr_converts_on_windows_and_is_no_download_away_elsewhere()
    {
        var (code, output, _) = await Run("formats", "jxr");

        Assert.Equal(Cli.Success, code);
        Assert.Equal(OperatingSystem.IsWindows(), output.Contains("png, jpg", StringComparison.Ordinal));
        // Elsewhere no download would help (on Windows, FFmpeg adds HEIC and video).
        Assert.Equal(OperatingSystem.IsWindows(), output.Contains("optional engines", StringComparison.Ordinal));
    }

    /// <summary>Adds presets to the settings folder the CLI runs with.</summary>
    private void AddPresets(params Preset[] presets)
    {
        var store = new Filee.Core.Settings.UserDataStore(_settings);
        store.Load();
        store.Presets.AddRange(presets);
        store.SaveLibrary();
    }

    [Fact]
    public async Task A_group_is_never_run_listed_or_in_the_way_of_a_conversion_of_the_same_name()
    {
        AddPresets(
            new Preset { Id = "web-group", Name = "Web", Kind = PresetKind.Group, SubPresetIds = ["to-png", "to-webp"] },
            new Preset { Id = "empty-group", Name = "Empty", Kind = PresetKind.Group },
            new Preset { Id = "my-web", Name = "Web", TargetFormat = "jpg" });
        var png = Png("a.png");

        // "Web" is both a group and a conversion: the conversion runs.
        var (code, _, errors) = await Run("convert", png, "--preset", "Web");
        Assert.Equal(Cli.Success, code);
        Assert.True(File.Exists(Path.Combine(_folder, "a.jpg")), errors);

        // A group by its id says what it holds; one with nothing left says so instead of "Use one of: ".
        (code, _, errors) = await Run("convert", png, "--preset", "web-group");
        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("Use one of: to-png, to-webp", errors);
        (code, _, errors) = await Run("convert", png, "--preset", "Empty");
        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("no presets left", errors);

        var (_, list, _) = await Run("presets");
        Assert.Contains("my-web", list);
        Assert.DoesNotContain("web-group", list);
    }

    [Fact]
    public async Task Data_files_convert_to_each_other()
    {
        var json = Path.Combine(_folder, "config.json");
        await File.WriteAllTextAsync(json, """{"name": "Filee", "ports": [80, 443]}""", TestContext.Current.CancellationToken);

        var (code, output, errors) = await Run("convert", json, "--to", "yaml");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Equal("name: Filee\nports:\n  - 80\n  - 443\n", await File.ReadAllTextAsync(Path.Combine(_folder, "config.yaml"), TestContext.Current.CancellationToken));

        (code, output, _) = await Run("formats", "json");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("yaml, toml, xml", output);
    }

    [Fact]
    public async Task Help_and_version_need_no_engines()
    {
        var (code, output, _) = await Run("help", "convert");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("--to", output);

        (code, output, _) = await Run("--version");
        Assert.Equal(Cli.Success, code);
        Assert.StartsWith("filee ", output);
    }

    [Fact]
    public async Task Case_distinct_inputs_are_both_converted_and_exact_duplicates_are_removed()
    {
        var lower = Png("photo.png");
        if (File.Exists(Path.Combine(_folder, "PHOTO.png")))
        {
            Assert.Skip("The temporary volume is case-insensitive.");
            return;
        }
        var upper = Png("PHOTO.png");
        var outputFolder = Path.Combine(_folder, "out");

        var (code, output, errors) = await Run("convert", lower, upper, lower, "--to", "jpg", "-o", outputFolder, "--json");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(2, json.RootElement.GetProperty("converted").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("files").GetArrayLength());
        var outputPaths = json.RootElement.GetProperty("files").EnumerateArray()
            .Select(file => file.GetProperty("outputs")[0].GetString()!).ToArray();
        Assert.Equal(2, outputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(outputPaths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task Unicode_spaces_and_quotes_stay_in_a_single_input_argument()
    {
        var name = OperatingSystem.IsWindows() ? "한글 report's 100%.png" : "한글 report's \"quoted\" 100%.png";
        var source = Png(name);
        var outputFolder = Path.Combine(_folder, "결과 output's");

        var (code, output, errors) = await Run("convert", source, "--to", "jpg", "-o", outputFolder, "--json");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(1, json.RootElement.GetProperty("converted").GetInt32());
        var result = json.RootElement.GetProperty("files")[0].GetProperty("outputs")[0].GetString();
        Assert.NotNull(result);
        Assert.Equal(outputFolder, Path.GetDirectoryName(result));
        Assert.Contains("한글 report's", Path.GetFileName(result));
        Assert.True(File.Exists(result));
        Assert.True(File.Exists(source));
    }

    // ──────────── Windows bitmap fonts (#177): in-memory samples shared with the engine tests ────────────

    private static readonly string[] FontFormats = ["ttf", "otf", "woff", "woff2", "eot"];

    /// <summary>Writes a synthetic sample to <c>Name.fon</c> in this test's folder (or a subfolder).</summary>
    private string Fon(string sample, string? folder = null)
    {
        folder = Path.Combine(_folder, folder ?? "");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Name.fon");
        File.WriteAllBytes(path, FonBuilder.Sample(sample));
        return path;
    }

    private static string[] Outputs(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.GetProperty("files")[0].GetProperty("outputs").EnumerateArray().Select(o => o.GetString()!)];
    }

    [Fact]
    public void Generated_fon_samples_are_written_to_cli_input()
    {
        foreach (var sample in new[] { "Fon-v2-fixed.fon", "Fon-multi.fon" })
        {
            var bytes = FonBuilder.Sample(sample);
            Assert.Equal("MZ"u8.ToArray(), bytes[..2]);
            Assert.Equal(bytes, File.ReadAllBytes(Fon(sample, Path.GetFileNameWithoutExtension(sample))));
        }
    }

    [Theory]
    [InlineData("ttf")]
    [InlineData("otf")]
    [InlineData("woff")]
    [InlineData("woff2")]
    [InlineData("eot")]
    public async Task Fon_single_resource_omits_size_suffix_in_all_five_formats(string format)
    {
        var source = Fon("Fon-v2-fixed.fon");
        var json = Path.Combine(_folder, "json");
        var text = Path.Combine(_folder, "text");

        var (code, output, errors) = await Run("convert", source, "--to", format, "-o", json, "--json");
        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Equal([Path.Combine(json, "Name." + format)], Outputs(output));
        Assert.Equal(["Name." + format], Directory.EnumerateFiles(json).Select(Path.GetFileName));

        (code, output, _) = await Run("convert", source, "--to", format, "-o", text);
        Assert.Equal(Cli.Success, code);
        Assert.Contains("Name." + format, output);
        Assert.DoesNotContain("13px", output);
        Assert.Equal(["Name." + format], Directory.EnumerateFiles(text).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Fon_reports_each_strike_and_obeys_conflicts()
    {
        var source = Fon("Fon-multi.fon");
        string[] names = ["Name-13px.ttf", "Name-13px-2.ttf", "Name-14px.ttf"];

        // No conflict, in each format: one file per RT_FONT resource, every path in the text and JSON output.
        foreach (var format in FontFormats)
        {
            var clean = Path.Combine(_folder, "clean-" + format);
            var (code, output, _) = await Run("convert", source, "--to", format, "-o", clean, "--json");
            Assert.Equal(Cli.Success, code);
            Assert.Equal(names.Select(n => Path.Combine(clean, Path.ChangeExtension(n, format))), Outputs(output));
        }
        var seed = Path.Combine(_folder, "clean-ttf");
        var (textCode, text, _) = await Run("convert", source, "--to", "ttf", "-o", Path.Combine(_folder, "text"));
        Assert.Equal(Cli.Success, textCode);
        Assert.All(names, n => Assert.Contains(n, text));

        // One size already there: skip writes the other two, rename and overwrite write three.
        string Folder(string name)
        {
            var folder = Path.Combine(_folder, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Name-13px.ttf"), "old");
            return folder;
        }

        var skip = Folder("skip");
        var (skipCode, skipped, _) = await Run("convert", source, "--to", "ttf", "-o", skip, "--skip", "--json");
        Assert.Equal(Cli.Success, skipCode);
        Assert.Equal([Path.Combine(skip, "Name-13px-2.ttf"), Path.Combine(skip, "Name-14px.ttf")], Outputs(skipped));
        Assert.Equal("old", File.ReadAllText(Path.Combine(skip, "Name-13px.ttf")));

        var rename = Folder("rename");
        var (renameCode, renamed, _) = await Run("convert", source, "--to", "ttf", "-o", rename, "--json");
        Assert.Equal(Cli.Success, renameCode);
        Assert.Equal([Path.Combine(rename, "Name-13px (2).ttf"), Path.Combine(rename, "Name-13px-2.ttf"), Path.Combine(rename, "Name-14px.ttf")],
            Outputs(renamed));
        Assert.Equal("old", File.ReadAllText(Path.Combine(rename, "Name-13px.ttf")));

        var overwrite = Folder("overwrite");
        var (overwriteCode, overwritten, _) = await Run("convert", source, "--to", "ttf", "-o", overwrite, "--overwrite", "--json");
        Assert.Equal(Cli.Success, overwriteCode);
        Assert.Equal(names.Select(n => Path.Combine(overwrite, n)), Outputs(overwritten));
        Assert.All(names, n => Assert.Equal(File.ReadAllBytes(Path.Combine(seed, n)), File.ReadAllBytes(Path.Combine(overwrite, n))));

        // Every size there: skipped, with no outputs.
        var (allCode, all, _) = await Run("convert", source, "--to", "ttf", "-o", seed, "--skip", "--json");
        Assert.Equal(Cli.Success, allCode);
        using var json = JsonDocument.Parse(all);
        Assert.Equal("skipped", json.RootElement.GetProperty("files")[0].GetProperty("state").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("files")[0].GetProperty("outputs").GetArrayLength());
    }

    [Fact]
    public async Task Fon_formats_are_read_only_and_no_to_fon()
    {
        var (code, output, _) = await Run("formats");
        Assert.Equal(Cli.Success, code);
        Assert.Contains(output.Split('\n'), line => line.TrimStart().StartsWith("fon ", StringComparison.Ordinal) && line.Contains("(read only)"));

        (code, output, _) = await Run("formats", "fon");
        Assert.Equal(Cli.Success, code);
        var targets = output.Split('\n')[1].Split(',', StringSplitOptions.TrimEntries);
        Assert.Equal(FontFormats.Order(), targets.Order());

        var (refused, _, errors) = await Run("convert", Fon("Fon-v2-fixed.fon"), "--to", "fon");
        Assert.Equal(Cli.UsageError, refused);
        Assert.Contains("can't write", errors);
        Assert.Equal(["Name.fon"], Directory.EnumerateFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Refuses_tool_presets()
    {
        var source = Png("photo.png");
        var (code, _, errors) = await Run("convert", source, "--preset", "tool-crop");

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("is an interactive tool, not a conversion", errors);
    }
}
