// The Metadata tool's engines: ExifTool reads every field grouped by where it lives, lists whole, and writes changes,
// removals, additions, Remove location and Remove everything into a copy without touching the picture, sound or
// document; what it would write otherwise than asked fails the copy instead. Its converter is the only engine a
// conversion with changes takes. The ExifTool tests need one on this computer (PATH or FILEE_TEST_EXIFTOOL).

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;
using Filee.Engines.Metadata;
using ImageMagick;

namespace Filee.Engines.Tests;

public class MetadataTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string? FindExifTool()
    {
        // Set and not there is a broken setup, as in CI: failing, not skipping, so the writer can't go untested unseen.
        if (Environment.GetEnvironmentVariable("FILEE_TEST_EXIFTOOL") is { Length: > 0 } chosen)
            return File.Exists(chosen) ? chosen : throw new FileNotFoundException("FILEE_TEST_EXIFTOOL is set, but no such file.", chosen);
        var name = OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"])
            .Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }

    private static string ExifToolOrSkip()
    {
        var exifTool = FindExifTool();
        if (exifTool is null)
            Assert.Skip("No ExifTool on this computer.");
        return exifTool;
    }

    private static MetadataEdits Setting(params (string Key, string[] Items)[] fields)
    {
        var edits = new MetadataEdits();
        foreach (var (key, items) in fields)
            edits.Set[key] = [.. items];
        return edits;
    }

    /// <summary>A small JPEG with camera, place and description fields, as a phone writes them.</summary>
    private async Task<(string Path, string ExifTool)> PhotoAsync()
    {
        var exifTool = ExifToolOrSkip();
        var folder = fx.NewFolder();
        var plain = Path.Combine(folder, "plain.jpg");
        using (var image = new MagickImage(new MagickColor("#C87A3C"), 64, 48))
        {
            image.Quality = 90;
            image.Write(plain, MagickFormat.Jpeg);
        }
        var camera = Path.Combine(folder, "camera.jpg");
        await ExifTool.WriteAsync(exifTool, plain, camera, Setting(
            ("IFD0:Make", ["Apple"]),
            ("IFD0:Model", ["iPhone 15 Pro"]),
            ("IFD0:Software", ["iOS 26.6"]),
            ("IFD0:Orientation", ["Rotate 90 CW"]),
            ("ExifIFD:DateTimeOriginal", ["2026:08:12 18:42:10"]),
            ("GPS:GPSLatitude", ["45.5017"]),
            ("GPS:GPSLatitudeRef", ["N"]),
            ("GPS:GPSLongitude", ["73.5673"]),
            ("GPS:GPSLongitudeRef", ["W"]),
            ("XMP-dc:Title", ["Old Port"]),
            ("XMP-photoshop:City", ["Montréal"])), Token);
        // IPTC in Latin-1, as most cameras and older apps write it.
        var path = Path.Combine(folder, "photo.jpg");
        await ExifTool.WriteAsync(exifTool, camera, path, Setting(("IPTC:Keywords", ["summer"]), ("IPTC:By-line", ["Zoë"])), Token);
        return (path, exifTool);
    }

    private static byte[] Pixels(string path)
    {
        using var image = new MagickImage(path);
        return image.GetPixels().ToByteArray(PixelMapping.RGB)!;
    }

    private static string? ValueOf(IReadOnlyList<MetadataField> fields, string key) => fields.FirstOrDefault(f => f.Key == key)?.Value;

    private string Target(string source, string name = "copy") =>
        Path.Combine(Path.GetDirectoryName(source)!, name + Path.GetExtension(source));

    [Fact]
    public async Task ExifTool_reads_every_field_by_group_and_marks_what_the_file_decides_read_only()
    {
        var (path, exifTool) = await PhotoAsync();

        var fields = await ExifTool.ReadAsync(exifTool, path, Token);

        Assert.Equal("Apple", ValueOf(fields, "IFD0:Make"));
        Assert.Equal("Rotate 90 CW", ValueOf(fields, "IFD0:Orientation"));
        Assert.Equal("Old Port", ValueOf(fields, "XMP-dc:Title"));
        Assert.Equal("Montréal", ValueOf(fields, "XMP-photoshop:City"));
        Assert.Equal("Zoë", ValueOf(fields, "IPTC:By-line"));
        Assert.Contains(fields, f => f is { Group: "GPS", Tag: "GPSLatitude", Family: "EXIF", Editable: true });
        Assert.Contains(fields, f => f is { Family: "File", Editable: false });
        Assert.DoesNotContain(fields, f => f.Family is "Composite" or "File" or "System" or "ExifTool" && f.Editable);
        // What ExifTool writes back by itself whenever it writes EXIF or IPTC is never offered for editing.
        Assert.DoesNotContain(fields, f => f.Tag is "XResolution" or "ResolutionUnit" or "ApplicationRecordVersion" && f.Editable);
    }

    [Fact]
    public void A_list_is_read_with_its_entries_whole_and_bytes_that_arent_text_are_never_offered_for_editing()
    {
        var fields = ExifTool.Parse("""
            [{"SourceFile": "a.jpg",
              "XMP:XMP-dc:Creator": ["Smith, John", "Kim"],
              "XMP:XMP-dc:Title": "Hello, world",
              "IPTC:Keywords": ["harbour", "boats"],
              "PDF:Author": "Kim, Alex",
              "File:FileType": "JPEG",
              "XMP:XMP-dc:Subject": "one keyword",
              "XMP:XMP-dc:Description": true,
              "QuickTime:MajorBrand": "qt",
              "QuickTime:Meta:PrimaryItemReference": 10,
              "QuickTime:Track4:HandlerType": "Video",
              "QuickTime:Keys:Title": "Kept",
              "PNG:Gamma": 2.2,
              "PNG:Title": "Shot",
              "EXIF:IFD0:StripOffsets": 8,
              "XMP:XMP-GContainer:DirectoryItemLength": [0, 5000],
              "XMP:XMP-Container:DirectoryItemMime": "video/mp4",
              "XMP:XMP-GCamera:MotionPhoto": 1,
              "XMP:XMP-GCamera:MicroVideoOffset": 5000,
              "XMP:XMP-GCamera:BurstID": "x",
              "EXIF:SubIFD1:ImageWidth": 8,
              "EXIF:SubIFD1:Make": "x",
              "EXIF:IFD0:ColorMatrix1": "0.5 0.5",
              "EXIF:IFD0:DNGVersion": "1.4.0.0",
              "EXIF:IFD2:Artist": "Kim",
              "EXIF:ExifIFD:SerialNumber": "123",
              "PNG:Datecreate": "2026",
              "PDF:TaggedPDF": "Yes",
              "PDF:PageMode": "UseOutlines",
              "PDF:Company": "Acme",
              "EXIF:IFD0:XResolution": 72,
              "EXIF:IFD0:Artist": "base64:/9j/4AAQ",
              "EXIF:IFD1:ThumbnailImage": "(Binary data 5000 bytes, use -b option to extract)"}]
            """);

        var creator = fields.Single(f => f.Key == "XMP-dc:Creator");
        Assert.Equal((true, true), (creator.IsList, creator.Editable));
        Assert.Equal(["Smith, John", "Kim"], creator.Items);
        Assert.Equal("Smith, John\nKim", creator.Value);
        Assert.Equal((false, "Hello, world"), (fields.Single(f => f.Key == "XMP-dc:Title").IsList, fields.Single(f => f.Key == "XMP-dc:Title").Value));
        Assert.Equal("72", ValueOf(fields, "IFD0:XResolution"));
        // A list of one comes as plain text: it is a list all the same.
        Assert.True(fields.Single(f => f.Key == "XMP-dc:Subject").IsList);
        // As ExifTool printed it.
        Assert.Equal("true", ValueOf(fields, "XMP-dc:Description"));
        // What can't be changed or taken out of a video, a HEIC, a TIFF, a PNG or a PDF is shown, not offered.
        foreach (var key in new[]
                 {
                     "QuickTime:MajorBrand", "Meta:PrimaryItemReference", "Track4:HandlerType", "PNG:Gamma", "PNG:Datecreate", "IFD0:StripOffsets",
                     "IFD0:ColorMatrix1", "IFD0:DNGVersion", "SubIFD1:Make", "XMP-GContainer:DirectoryItemLength", "XMP-Container:DirectoryItemMime",
                     "XMP-GCamera:MotionPhoto", "XMP-GCamera:MicroVideoOffset", "PDF:TaggedPDF", "PDF:PageMode", "PDF:Company",
                 })
            Assert.False(fields.Single(f => f.Key == key).Editable, key);
        Assert.True(fields.Single(f => f.Key == "Keys:Title").Editable);
        Assert.True(fields.Single(f => f.Key == "PNG:Title").Editable);
        Assert.True(fields.Single(f => f.Key == "IFD2:Artist").Editable);
        Assert.True(fields.Single(f => f.Key == "XMP-GCamera:BurstID").Editable);
        Assert.True(fields.Single(f => f.Key == "ExifIFD:SerialNumber").Editable);
        // Family and group of the same name come as one: IPTC's and a PDF's own fields, the file's facts.
        Assert.Contains(fields, f => f is { Family: "IPTC", Group: "IPTC", Tag: "Keywords", IsList: true, Editable: true });
        Assert.Contains(fields, f => f is { Family: "PDF", Group: "PDF", Tag: "Author", Editable: true } && f.Value == "Kim, Alex");
        Assert.Contains(fields, f => f is { Family: "File", Group: "File", Tag: "FileType", Editable: false });
        Assert.False(fields.Single(f => f.Key == "IFD0:Artist").Editable);
        Assert.False(fields.Single(f => f.Key == "IFD1:ThumbnailImage").Editable);
    }

    [Fact]
    public async Task Changes_removals_and_additions_go_into_a_copy_and_the_picture_stays_as_it_was()
    {
        var (path, exifTool) = await PhotoAsync();
        var target = Target(path);
        var edits = Setting(
            ("IFD0:Model", ["iPhone 16"]),
            ("XMP-dc:Description", ["Line one\nLine two \\ with a backslash -and -a dash, mail a@b.com $HOME"]),
            ("IFD0:Copyright", ["© 2026 Alex Kim · 한국어"]),
            ("IFD0:Make", ["Nikon $HOME a@b"]));
        edits.Remove.Add("IFD0:Software");

        var warnings = await ExifTool.WriteAsync(exifTool, path, target, edits, Token);

        var copy = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.Equal("iPhone 16", ValueOf(copy, "IFD0:Model"));
        Assert.Equal("© 2026 Alex Kim · 한국어", ValueOf(copy, "IFD0:Copyright"));
        Assert.Equal("Line one\nLine two \\ with a backslash -and -a dash, mail a@b.com $HOME", ValueOf(copy, "XMP-dc:Description"));
        Assert.Null(ValueOf(copy, "IFD0:Software"));
        Assert.Equal("Nikon $HOME a@b", ValueOf(copy, "IFD0:Make"));
        Assert.Equal("Rotate 90 CW", ValueOf(copy, "IFD0:Orientation"));
        // The original is as it was; the picture data is the same in both.
        Assert.Equal("iOS 26.6", ValueOf(await ExifTool.ReadAsync(exifTool, path, Token), "IFD0:Software"));
        Assert.Equal(Pixels(path), Pixels(target));
        Assert.Null(warnings);
    }

    [Fact]
    public async Task A_list_is_written_entry_by_entry_so_a_comma_stays_inside_its_entry()
    {
        var (path, exifTool) = await PhotoAsync();
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, path, target, Setting(
            ("XMP-dc:Creator", ["Smith, John", "Kim"]),
            ("XMP-dc:Subject", ["line one\nline two", " padded ", "plain"]),
            ("XMP-dc:Title", ["Hello, world"]),
            ("IPTC:Keywords", ["harbour, evening", "boats"])), Token);

        var copy = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.Equal(["Smith, John", "Kim"], copy.Single(f => f.Key == "XMP-dc:Creator").Items);
        Assert.Equal(["line one\nline two", " padded ", "plain"], copy.Single(f => f.Key == "XMP-dc:Subject").Items);
        Assert.Equal("Hello, world", ValueOf(copy, "XMP-dc:Title"));
        // The list's entries are replaced, not added to: "summer" is gone.
        Assert.Equal(["harbour, evening", "boats"], copy.Single(f => f.Key == "IPTC:Keywords").Items);

        // A list emptied is removed.
        var emptied = Target(path, "emptied");
        await ExifTool.WriteAsync(exifTool, target, emptied, Setting(("XMP-dc:Creator", [])), Token);
        Assert.Null(ValueOf(await ExifTool.ReadAsync(exifTool, emptied, Token), "XMP-dc:Creator"));
    }

    [Fact]
    public async Task Remove_everything_with_new_IPTC_text_outside_Latin_1_writes_its_character_set_again()
    {
        var (path, exifTool) = await PhotoAsync();
        var utf8 = Target(path, "utf8");
        await ExifTool.WriteAsync(exifTool, path, utf8, Setting(("IPTC:ObjectName", ["Порт"])), Token);
        var target = Target(path, "clean");
        var edits = Setting(("IPTC:ObjectName", ["李"]));
        edits.RemoveAll = true;

        await ExifTool.WriteAsync(exifTool, utf8, target, edits, Token);

        var copy = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.Equal("李", ValueOf(copy, "IPTC:ObjectName"));
        Assert.Contains("UTF8", ValueOf(copy, "IPTC:CodedCharacterSet"), StringComparison.Ordinal);
    }

    [Fact]
    public void Location_out_of_reach_is_a_place_kept_whole_and_not_a_cameras_focus_point()
    {
        MetadataField Field(string family, string group, string tag, bool editable = false) => new(family, group, tag, "x", editable);

        Assert.Equal(["City", "Country", "City2", "LocalLocationName", "Location", "ExifGPSLatitude", "LocationCity", "ContentLocation", "GPSLatitude", "SigningLocation", "Altitude", "GeoTiffDirectory"],
            ExifTool.LocationOutOfReach(
        [
            Field("MakerNotes", "Panasonic", "City"), Field("MakerNotes", "Panasonic", "Country"), Field("MakerNotes", "Panasonic", "City2"),
            Field("MakerNotes", "Samsung", "LocalLocationName"), Field("JUMBF", "JSON", "Location"), Field("JUMBF", "JSON", "ExifGPSLatitude"),
            Field("JUMBF", "JSON", "LocationCity"), Field("JUMBF", "JSON", "ContentLocation"), Field("MakerNotes", "Sony", "GPSLatitude"),
            Field("MakerNotes", "Canon", "City"), Field("PDF", "PDF", "SigningLocation"), Field("PNG", "PNG", "Location"),
            Field("MakerNotes", "Nikon", "Altitude"), Field("GeoTiff", "GeoTiff", "GeogCitation"), Field("GeoTiff", "GeoTiff", "Projection"),
        ]));
        Assert.Empty(ExifTool.LocationOutOfReach(
        [
            // What Remove location takes, whether the window edits it or not.
            Field("EXIF", "GPS", "GPSLatitude", editable: true), Field("EXIF", "GPS", "GPSProcessingMethod"), Field("XMP", "XMP-photoshop", "City", editable: true),
            Field("QuickTime", "Keys", "GPSCoordinates", editable: true),
            // Worked out from the file.
            Field("Composite", "Composite", "GPSPosition"),
            // No place.
            Field("MakerNotes", "Sony", "FocusLocation"), Field("MakerNotes", "Pentax", "WorldTimeLocation"), Field("Photoshop", "Photoshop", "GlobalAltitude"),
            Field("MakerNotes", "Pentax", "HometownCity"), Field("EXIF", "ExifIFD", "SubjectLocation"),
            Field("XML", "XML-xsi", "ImageCreationSchemaLocation"), Field("MakerNotes", "Nikon", "LocationInfoVersion"), Field("MakerNotes", "Sony", "MemoryAllocation"),
            Field("MakerNotes", "Canon", "BorderLocation"), Field("MakerNotes", "Canon", "BlockLocation"), Field("MakerNotes", "Canon", "XYLocation"),
            Field("MakerNotes", "Nikon", "SetClockFromLocationData"), Field("MakerNotes", "Canon", "ExtraTickLocations"),
            // A place not set: blank, or Panasonic's "---".
            new("MakerNotes", "Panasonic", "State", "", false), new("MakerNotes", "Panasonic", "Landmark", "  ", false), new("MakerNotes", "Panasonic", "Country", "---", false),
        ]));
    }

    [Theory]
    [InlineData("#!/bin/sh", false)]
    [InlineData("#!/usr/bin/env sh", false)]
    [InlineData("#!/usr/bin/env -S sh -e", false)]
    [InlineData("#!/no/such/perl -w", true)]
    [InlineData("#!/usr/bin/env perl-not-here", true)]
    [InlineData("#!/usr/bin/env PERL5LIB=/x perl-not-here", true)]
    [InlineData("#!/usr/bin/env -u NAME sh", false)]
    [InlineData("#!/usr/bin/env PERL5LIB=/usr/share/perl5 sh", false)]
    [InlineData("#!/usr/bin/env -C /opt/perl5 sh", false)]
    [InlineData("#!/usr/bin/env PERL5LIB=/usr/share/perl5 perl-not-here", true)]
    [InlineData("not a script", false)]
    public void A_script_with_nothing_to_run_it_needs_Perl_and_a_program_does_not(string firstLine, bool needsPerl)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "ExifTool is a program on Windows.");
        var script = Path.Combine(fx.NewFolder(), "exiftool");
        File.WriteAllText(script, firstLine + "\nprint 1;\n");

        Assert.Equal(needsPerl, ExifTool.NeedsPerl(script));
    }

    [Fact]
    public void A_perl_in_a_folder_with_an_accented_name_is_found()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "ExifTool is a program on Windows.");
        var folder = Directory.CreateDirectory(Path.Combine(fx.NewFolder(), "jörg")).FullName;
        var perl = Path.Combine(folder, "perl");
        File.WriteAllText(perl, "");
        var script = Path.Combine(folder, "exiftool");
        File.WriteAllText(script, $"#!{perl}\nprint 1;\n");

        Assert.False(ExifTool.NeedsPerl(script));
    }

    [Fact]
    public void No_file_at_all_is_not_a_missing_Perl() => Assert.False(ExifTool.NeedsPerl("/no/such/exiftool"));

    [Fact]
    public async Task IPTC_text_outside_Latin_1_is_written_in_UTF_8_and_the_files_Latin_1_text_stays_readable()
    {
        var (path, exifTool) = await PhotoAsync();
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, path, target, Setting(("IPTC:Keywords", ["한국어", "summer"]), ("IPTC:ObjectName", ["Порт"])), Token);

        var copy = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.Equal(["한국어", "summer"], copy.Single(f => f.Key == "IPTC:Keywords").Items);
        Assert.Equal("Порт", ValueOf(copy, "IPTC:ObjectName"));
        // Written in Latin-1 before, read the same in UTF-8 now: not "Zo?".
        Assert.Equal("Zoë", ValueOf(copy, "IPTC:By-line"));
        Assert.Contains("UTF8", ValueOf(copy, "IPTC:CodedCharacterSet"), StringComparison.Ordinal);

        // Already UTF-8: written as it is, nothing else touched.
        var again = Target(path, "again");
        await ExifTool.WriteAsync(exifTool, target, again, Setting(("IPTC:By-line", ["Zoë Ü 李"])), Token);
        var second = await ExifTool.ReadAsync(exifTool, again, Token);
        Assert.Equal("Zoë Ü 李", ValueOf(second, "IPTC:By-line"));
        Assert.Equal(["한국어", "summer"], second.Single(f => f.Key == "IPTC:Keywords").Items);
    }

    public static TheoryData<string, string> Refused => new()
    {
        { "IFD0:NoSuchTag", "x" },                                 // unknown
        { "ExifIFD:DateTimeOriginal", "last summer" },             // a value it can't take
        { "IFD0:Orientation", "sideways" },
        { "PDF:Title", "x" },                                      // no such place in a JPEG: copied without a word
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_change_ExifTool_would_not_write_as_asked_fails_the_copy_and_leaves_none(string key, string value)
    {
        var (path, exifTool) = await PhotoAsync();
        var target = Target(path);

        var error = await Assert.ThrowsAsync<MetadataRefusedException>(() => ExifTool.WriteAsync(exifTool, path, target, Setting((key, [value])), Token));

        Assert.Contains(key.Split(':')[1], error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task IPTC_text_longer_than_its_field_is_meant_to_hold_is_written_whole_not_cut_short()
    {
        var (path, exifTool) = await PhotoAsync();
        var target = Target(path);
        var name = new string('x', 80);

        await ExifTool.WriteAsync(exifTool, path, target, Setting(("IPTC:ObjectName", [name])), Token);

        Assert.Equal(name, ValueOf(await ExifTool.ReadAsync(exifTool, target, Token), "IPTC:ObjectName"));
    }

    [Fact]
    public async Task Remove_location_takes_every_place_field_and_keeps_the_rest()
    {
        var (path, exifTool) = await PhotoAsync();
        var placed = Target(path, "placed");
        await ExifTool.WriteAsync(exifTool, path, placed, Setting(
            ("XMP-exif:GPSLatitude", ["45.5"]),
            ("XMP-iptcCore:Location", ["Old Port"]),
            ("XMP-iptcCore:CountryCode", ["CA"]),
            ("XMP-iptcExt:LocationCreatedCity", ["Montréal"]),
            ("XMP-iptcExt:LocationShownCity", ["Montréal"]),
            ("XMP-photoshop:State", ["QC"]),
            ("XMP-photoshop:Country", ["Canada"]),
            ("IPTC:City", ["Montréal"]),
            ("IPTC:Sub-location", ["Old Port"]),
            ("IPTC:Province-State", ["QC"]),
            ("IPTC:Country-PrimaryLocationName", ["Canada"])), Token);
        var before = await ExifTool.ReadAsync(exifTool, placed, Token);
        Assert.True(before.Count(ExifTool.IsLocation) >= 14, string.Join(", ", before.Where(ExifTool.IsLocation).Select(f => f.Key)));
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, placed, target, new MetadataEdits { RemoveLocation = true }, Token);

        var fields = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.DoesNotContain(fields, f => !ExifTool.NotInTheFile.Contains(f.Group) && ExifTool.IsLocation(f));
        Assert.Equal("Apple", ValueOf(fields, "IFD0:Make"));
        Assert.Equal("Old Port", ValueOf(fields, "XMP-dc:Title"));
        Assert.Equal("Zoë", ValueOf(fields, "IPTC:By-line"));
    }

    [Theory]
    [InlineData("GPSLatitude", true)]
    [InlineData("GpsLatitude", true)]
    [InlineData("LocationInformation", true)]
    [InlineData("LocationCreatedCity", true)]
    [InlineData("ContentLocationName", true)]
    [InlineData("CountryCode", true)]
    [InlineData("Sub-location", true)]
    [InlineData("Make", false)]
    [InlineData("CreatorCity", false)]
    public void What_says_where_the_file_was_made_is_one_rule(string tag, bool place) =>
        Assert.Equal(place, ExifTool.IsLocation(new MetadataField("XMP", "XMP-x", tag, "v", true)));

    [Fact]
    public async Task A_video_loses_its_place_in_every_box_it_keeps_one_and_its_streams_stay_as_they_were()
    {
        var exifTool = ExifToolOrSkip();
        Assert.SkipUnless(FfmpegConverter.Locate() is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");
        var (ffmpeg, _) = FfmpegConverter.Locate()!.Value;
        var folder = fx.NewFolder();
        foreach (var name in new[] { "clip.mp4", "clip.mov" })
        {
            var video = Path.Combine(folder, name);
            var made = await ProcessRunner.RunAsync(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=5:duration=1", "-pix_fmt", "yuv420p",
                "-metadata", "location=+45.5017-073.5673/", "-metadata", "title=Old", video], TimeSpan.FromMinutes(1), Token);
            Assert.True(made.ExitCode == 0, made.StandardError);
            Assert.Contains(await ExifTool.ReadAsync(exifTool, video, Token), ExifTool.IsLocation);

            var placeless = Target(video, "placeless");
            await ExifTool.WriteAsync(exifTool, video, placeless, new MetadataEdits { RemoveLocation = true }, Token);
            Assert.DoesNotContain(await ExifTool.ReadAsync(exifTool, placeless, Token), f => !ExifTool.NotInTheFile.Contains(f.Group) && ExifTool.IsLocation(f));
            Assert.Equal(await StreamsAsync(ffmpeg, video), await StreamsAsync(ffmpeg, placeless));

            var titled = Target(video, "titled");
            await ExifTool.WriteAsync(exifTool, video, titled, Setting(("XMP-dc:Title", ["Harbour, evening"])), Token);
            Assert.Equal("Harbour, evening", ValueOf(await ExifTool.ReadAsync(exifTool, titled, Token), "XMP-dc:Title"));
            Assert.Equal(await StreamsAsync(ffmpeg, video), await StreamsAsync(ffmpeg, titled));

            var cleared = Target(video, "cleared");
            await ExifTool.WriteAsync(exifTool, video, cleared, new MetadataEdits { RemoveAll = true }, Token);
            Assert.DoesNotContain(await ExifTool.ReadAsync(exifTool, cleared, Token), f => f.Editable);
            Assert.Equal(await StreamsAsync(ffmpeg, video), await StreamsAsync(ffmpeg, cleared));
            foreach (var done in new[] { placeless, titled, cleared })
                File.Delete(done);
        }
    }

    /// <summary>A digest of a video's streams as they are stored, whatever is around them.</summary>
    private static async Task<string> StreamsAsync(string ffmpeg, string video)
    {
        var result = await ProcessRunner.RunAsync(ffmpeg, ["-v", "error", "-i", video, "-map", "0:v", "-c", "copy", "-f", "md5", "-"], TimeSpan.FromMinutes(1), Token);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }

    [Theory]
    [InlineData("shot.jpg", MagickFormat.Jpeg)]
    [InlineData("shot.png", MagickFormat.Png)]
    [InlineData("shot.tif", MagickFormat.Tiff)]
    [InlineData("shot.webp", MagickFormat.WebP)]
    public async Task Remove_everything_leaves_no_field_in_any_kind_of_picture_and_the_picture_the_same(string name, MagickFormat format)
    {
        var exifTool = ExifToolOrSkip();
        var folder = fx.NewFolder();
        var plain = Path.Combine(folder, "plain-" + name);
        using (var image = new MagickImage(new MagickColor("#3C7AC8"), 40, 30))
        {
            image.Quality = 100;
            image.Settings.Compression = format == MagickFormat.WebP ? CompressionMethod.WebP : image.Settings.Compression;
            image.Write(plain, format);
        }
        var path = Path.Combine(folder, name);
        await ExifTool.WriteAsync(exifTool, plain, path, Setting(("IFD0:Artist", ["Kim"]), ("IFD0:Make", ["Canon"]), ("IFD0:Copyright", ["© Kim"]),
            ("ExifIFD:SerialNumber", ["123"]), ("GPS:GPSLatitude", ["10"]), ("GPS:GPSLatitudeRef", ["N"]), ("XMP-dc:Title", ["Title"]), ("IFD0:Orientation", ["Rotate 90 CW"])), Token);
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, path, target, new MetadataEdits { RemoveAll = true }, Token);

        var fields = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.Equal([], fields.Where(f => f.Editable && !ExifTool.KeptByRemoveAll(f)).Select(f => f.Key));
        Assert.Equal("Rotate 90 CW", ValueOf(fields, "IFD0:Orientation"));
        Assert.Equal(Pixels(path), Pixels(target));
    }

    [Fact]
    public async Task Remove_everything_reaches_every_page_of_a_TIFF_and_leaves_its_pictures()
    {
        var exifTool = ExifToolOrSkip();
        var tiff = EngineFixture.MakeMultiPageTiff(fx.NewFolder(), 3);
        var path = Target(tiff, "signed");
        await ExifTool.WriteAsync(exifTool, tiff, path, Setting(("IFD0:Artist", ["Kim"]), ("IFD1:Artist", ["Kim"]), ("IFD1:Software", ["Secret"]), ("IFD2:Artist", ["Kim"])), Token);
        var target = Target(tiff);

        await ExifTool.WriteAsync(exifTool, path, target, new MetadataEdits { RemoveAll = true }, Token);

        Assert.DoesNotContain(await ExifTool.ReadAsync(exifTool, target, Token), f => f.Tag is "Artist" or "Software");
        using var pages = new MagickImageCollection(target);
        Assert.Equal(3, pages.Count);
    }

    [Fact]
    public async Task Remove_everything_takes_a_TIFFs_descriptions_and_leaves_what_says_how_to_read_it()
    {
        var exifTool = ExifToolOrSkip();
        var tiff = EngineFixture.MakeImage(fx.NewFolder(), "plain.tif", MagickFormat.Tiff);
        var path = Target(tiff, "history");
        await ExifTool.WriteAsync(exifTool, tiff, path, Setting(("IFD0:ImageHistory", ["Edited by Jane at 12 Main St"]), ("IFD0:ProcessingSoftware", ["Jane's app"]),
            ("IFD0:Orientation", ["Rotate 180"])), Token);
        var target = Target(tiff);

        await ExifTool.WriteAsync(exifTool, path, target, new MetadataEdits { RemoveAll = true }, Token);

        var fields = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.DoesNotContain(fields, f => f.Tag is "ImageHistory" or "ProcessingSoftware");
        Assert.Equal("Rotate 180", ValueOf(fields, "IFD0:Orientation"));
        Assert.Equal(Pixels(path), Pixels(target));
    }

    [Fact]
    public async Task Whats_after_the_picture_is_left_alone_and_a_file_holding_more_is_known_by_its_fields()
    {
        var (path, exifTool) = await PhotoAsync();
        byte[] video = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 % 251))];
        var motion = Target(path, "motion");
        File.Copy(path, motion);
        await using (var file = new FileStream(motion, FileMode.Append))
            await file.WriteAsync(video, Token);
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, motion, target, new MetadataEdits { RemoveAll = true }, Token);

        Assert.Equal(video, (await File.ReadAllBytesAsync(target, Token))[^video.Length..]);
        Assert.False(ExifTool.HoldsMore(await ExifTool.ReadAsync(exifTool, path, Token)));
        foreach (var (family, group, tag) in new[]
                 {
                     ("MPF", "MPF0", "NumberOfImages"), ("XMP", "XMP-GCamera", "MicroVideo"), ("XMP", "XMP-GCamera", "MotionPhoto"), ("XMP", "XMP-Container", "DirectoryItemMime"),
                     ("XMP", "XMP-hdrgm", "Version"), ("MakerNotes", "Samsung", "EmbeddedVideoFile"), ("MakerNotes", "Samsung", "MCCData"), ("Trailer", "Trailer", "Anything"),
                 })
            Assert.True(ExifTool.HoldsMore([new MetadataField(family, group, tag, "1", false)]), $"{group}:{tag}");
        // A Samsung's or a Pixel's own maker notes and XMP are no such thing, nor a photo that says it has no video.
        Assert.False(ExifTool.HoldsMore(
        [
            new MetadataField("MakerNotes", "Samsung", "DeviceType", "x", false), new MetadataField("XMP", "XMP-GCamera", "BurstID", "x", true),
            new MetadataField("XMP", "XMP-GCamera", "MicroVideo", "0", true),
        ]));
    }

    [Fact]
    public async Task The_orientation_stays_where_it_was_and_a_thumbnails_never_becomes_the_pictures()
    {
        var (path, exifTool) = await PhotoAsync();
        var thumbnail = Target(path, "thumbnail");
        await ExifTool.WriteAsync(exifTool, path, thumbnail, new MetadataEdits { Remove = ["IFD0:Orientation"], Set = { ["IFD1:Orientation"] = ["Rotate 90 CW"] } }, Token);
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, thumbnail, target, new MetadataEdits { RemoveAll = true }, Token);

        Assert.Null(ValueOf(await ExifTool.ReadAsync(exifTool, target, Token), "IFD0:Orientation"));
    }

    [Theory]
    [InlineData("note.jpg", MagickFormat.Jpeg)]
    [InlineData("note.gif", MagickFormat.Gif)]
    [InlineData("note.ppm", MagickFormat.Ppm)]
    public async Task A_files_own_comment_is_a_field_like_any_other(string name, MagickFormat format)
    {
        var exifTool = ExifToolOrSkip();
        var folder = fx.NewFolder();
        var path = EngineFixture.MakeImage(folder, name, format);
        var commented = Target(path, "commented");
        await ExifTool.WriteAsync(exifTool, path, commented, Setting(("File:Comment", ["Taken by Jane at 12 Main St"])), Token);
        var field = (await ExifTool.ReadAsync(exifTool, commented, Token)).Single(f => f.Key == "File:Comment");
        Assert.True(field.Editable);

        var removed = Target(path, "removed");
        await ExifTool.WriteAsync(exifTool, commented, removed, new MetadataEdits { Remove = ["File:Comment"] }, Token);
        Assert.Null(ValueOf(await ExifTool.ReadAsync(exifTool, removed, Token), "File:Comment"));

        var cleared = Target(path, "cleared");
        await ExifTool.WriteAsync(exifTool, commented, cleared, new MetadataEdits { RemoveAll = true }, Token);
        Assert.Null(ValueOf(await ExifTool.ReadAsync(exifTool, cleared, Token), "File:Comment"));
    }

    [Fact]
    public async Task An_orientation_removed_by_hand_goes_even_with_Remove_everything()
    {
        var (path, exifTool) = await PhotoAsync();
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, path, target, new MetadataEdits { RemoveAll = true, Remove = ["IFD0:Orientation"] }, Token);

        Assert.Null(ValueOf(await ExifTool.ReadAsync(exifTool, target, Token), "IFD0:Orientation"));
    }

    [Fact]
    public async Task A_private_file_stays_private_and_the_work_folder_is_one_the_watchers_pass_over()
    {
        Assert.True(Filee.Core.Presets.FolderCopy.IsWorkPath(Path.Combine("folder", Filee.Core.Presets.FolderCopy.WorkName("exiftool"), "copy.jpg")));
        if (OperatingSystem.IsWindows())
            return; // no such modes
        var (path, exifTool) = await PhotoAsync();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, path, target, Setting(("IFD0:Model", ["New"])), Token);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
    }

    [Fact]
    public async Task Remove_everything_gets_past_what_only_says_how_a_PDF_opens_and_an_orientation_kept_in_XMP()
    {
        var exifTool = ExifToolOrSkip();
        var folder = fx.NewFolder();
        // A catalog with a page mode and a layout, as LaTeX's hyperref writes it.
        var offsets = new List<int>();
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        foreach (var body in new[]
                 {
                     "<< /Type /Catalog /Pages 2 0 R /PageMode /UseOutlines /PageLayout /SinglePage >>",
                     "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                     "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
                     "<< /Title (Report) /Author (Kim) >>",
                 })
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 4 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(folder, "report.pdf");
        await File.WriteAllTextAsync(path, pdf.ToString(), Token);
        Assert.Contains(await ExifTool.ReadAsync(exifTool, path, Token), f => f is { Tag: "PageMode", Editable: false });
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, path, target, new MetadataEdits { RemoveAll = true }, Token);

        Assert.Null(ValueOf(await ExifTool.ReadAsync(exifTool, target, Token), "PDF:Author"));

        var gif = EngineFixture.MakeImage(folder, "plain.gif", MagickFormat.Gif);
        var turned = Target(gif, "turned");
        await ExifTool.WriteAsync(exifTool, gif, turned, Setting(("XMP-tiff:Orientation", ["Rotate 90 CW"]), ("XMP-dc:Title", ["Title"])), Token);
        var cleared = Target(gif, "cleared");
        await ExifTool.WriteAsync(exifTool, turned, cleared, new MetadataEdits { RemoveAll = true }, Token);
        var fields = await ExifTool.ReadAsync(exifTool, cleared, Token);
        Assert.Null(ValueOf(fields, "XMP-dc:Title"));
        Assert.Equal("Rotate 90 CW", ValueOf(fields, "XMP-tiff:Orientation"));
    }

    [Fact]
    public async Task Remove_location_passes_over_a_field_it_cant_take_which_stays_in_plain_sight()
    {
        var exifTool = ExifToolOrSkip();
        var folder = fx.NewFolder();
        var plain = Path.Combine(folder, "plain.png");
        using (var image = new MagickImage(MagickColors.Coral, 20, 20))
        {
            // A text chunk of its own, as with a camera's "FocusLocation": ExifTool can't remove it by name.
            image.SetAttribute("Location", "somewhere");
            image.Write(plain, MagickFormat.Png);
        }
        var path = Path.Combine(folder, "shot.png");
        await ExifTool.WriteAsync(exifTool, plain, path, Setting(("XMP-exif:GPSLatitude", ["45.5"])), Token);
        var before = await ExifTool.ReadAsync(exifTool, path, Token);
        Assert.Contains(before, f => f is { Family: "PNG", Tag: "Location", Editable: false });
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, path, target, new MetadataEdits { RemoveLocation = true }, Token);

        var fields = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.DoesNotContain(fields, f => f.Editable && ExifTool.IsLocation(f));
        Assert.Contains(fields, f => f is { Family: "PNG", Tag: "Location" });
    }

    [Fact]
    public async Task Remove_location_takes_the_GPS_fields_ExifTool_cant_remove_by_name_and_leaves_the_rest_of_their_group()
    {
        var (path, exifTool) = await PhotoAsync();
        var folder = Path.GetDirectoryName(path)!;
        // As macOS writes XMP: GPS…Ref in XMP-exif, which ExifTool reads but won't write.
        var xmp = Path.Combine(folder, "apple.xmp");
        await File.WriteAllTextAsync(xmp, """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:exif="http://ns.adobe.com/exif/1.0/" exif:GPSLatitude="45,30.00N" exif:GPSLatitudeRef="N" exif:ExposureTime="1/60"/>
            </rdf:RDF></x:xmpmeta>
            """, Token);
        var apple = Target(path, "apple");
        await ExifTool.WriteAsync(exifTool, path, apple, new MetadataEdits { Set = { ["XMP-dc:Rights"] = ["x"] } }, Token);
        var loaded = await ProcessRunner.RunAsync(exifTool, ["-q", "-overwrite_original", "-XMP<=" + xmp, apple], TimeSpan.FromMinutes(1), Token);
        Assert.True(loaded.ExitCode == 0, loaded.StandardError);
        Assert.Contains(await ExifTool.ReadAsync(exifTool, apple, Token), f => f.Key == "XMP-exif:GPSLatitudeRef");
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, apple, target, new MetadataEdits { RemoveLocation = true }, Token);

        var fields = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.DoesNotContain(fields, f => f.Group == "XMP-exif" && ExifTool.IsLocation(f));
        Assert.Equal("1/60", ValueOf(fields, "XMP-exif:ExposureTime"));

        // A list in that group changed at the same time is replaced, not added to what comes back.
        var edited = Target(path, "edited");
        await ExifTool.WriteAsync(exifTool, apple, edited, new MetadataEdits { RemoveLocation = true, Set = { ["XMP-exif:ISO"] = ["300", "400"], ["XMP-exif:ExposureTime"] = ["1/30"] } }, Token);
        var changed = await ExifTool.ReadAsync(exifTool, edited, Token);
        Assert.Equal(["300", "400"], changed.Single(f => f.Key == "XMP-exif:ISO").Items);
        Assert.Equal("1/30", ValueOf(changed, "XMP-exif:ExposureTime"));

        // Removed by hand as well; and a location of another kind in that group goes with the GPS.
        var subject = Target(path, "subject");
        await ExifTool.WriteAsync(exifTool, apple, subject, Setting(("XMP-exif:SubjectLocation", ["10", "20"])), Token);
        var removed = Target(path, "removed");
        await ExifTool.WriteAsync(exifTool, subject, removed, new MetadataEdits { RemoveLocation = true, Remove = ["XMP-exif:ExposureTime"] }, Token);
        var gone = await ExifTool.ReadAsync(exifTool, removed, Token);
        Assert.DoesNotContain(gone, f => f.Group == "XMP-exif" && (ExifTool.IsLocation(f) || f.Tag == "ExposureTime"));
    }

    [Fact]
    public async Task The_rest_of_a_files_XMP_exif_is_not_touched_and_its_lists_are_edited_as_ever()
    {
        var (path, exifTool) = await PhotoAsync();
        var plain = Target(path, "plain");
        await ExifTool.WriteAsync(exifTool, path, plain, Setting(("XMP-exif:ISO", ["100", "200"]), ("XMP-exif:SubjectLocation", ["1", "2"]), ("XMP-exif:GPSLatitude", ["45.5"])), Token);
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, plain, target, new MetadataEdits { RemoveLocation = true, Set = { ["XMP-exif:ISO"] = ["800"] } }, Token);

        var fields = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.Equal("800", ValueOf(fields, "XMP-exif:ISO"));
        Assert.DoesNotContain(fields, f => f.Group == "XMP-exif" && ExifTool.IsLocation(f));

        // What ExifTool couldn't write back, as macOS's time offsets, stays as it is: nothing rewrites it.
        var xmp = Path.Combine(Path.GetDirectoryName(path)!, "odd.xmp");
        await File.WriteAllTextAsync(xmp, """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:exif="http://ns.adobe.com/exif/1.0/" exif:GPSLatitude="45,30.00N" exif:GPSLatitudeRef="N" exif:OffsetTime="-05:00" exif:WhiteBalance="weird"/>
            </rdf:RDF></x:xmpmeta>
            """, Token);
        var odd = Target(path, "odd");
        File.Copy(path, odd);
        var loaded = await ProcessRunner.RunAsync(exifTool, ["-q", "-overwrite_original", "-XMP<=" + xmp, odd], TimeSpan.FromMinutes(1), Token);
        Assert.True(loaded.ExitCode == 0, loaded.StandardError);
        var kept = Target(path, "kept");

        await ExifTool.WriteAsync(exifTool, odd, kept, new MetadataEdits { RemoveLocation = true }, Token);

        var left = await ExifTool.ReadAsync(exifTool, kept, Token);
        Assert.Contains("weird", ValueOf(left, "XMP-exif:WhiteBalance"), StringComparison.Ordinal);
        Assert.Equal("-05:00", ValueOf(left, "XMP-exif:OffsetTime"));
        Assert.DoesNotContain(left, f => f.Group == "XMP-exif" && ExifTool.IsLocation(f));
    }

    [Fact]
    public async Task Every_page_of_a_TIFF_keeps_its_orientation_and_a_PNG_how_its_colours_are_read()
    {
        var exifTool = ExifToolOrSkip();
        var folder = fx.NewFolder();
        var tiff = EngineFixture.MakeMultiPageTiff(folder, 2);
        var turned = Target(tiff, "turned");
        await ExifTool.WriteAsync(exifTool, tiff, turned, Setting(("IFD1:Orientation", ["Rotate 90 CW"]), ("IFD1:Artist", ["Kim"])), Token);
        var target = Target(tiff);
        await ExifTool.WriteAsync(exifTool, turned, target, new MetadataEdits { RemoveAll = true }, Token);
        var fields = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.Equal("Rotate 90 CW", ValueOf(fields, "IFD1:Orientation"));
        Assert.Null(ValueOf(fields, "IFD1:Artist"));

        var png = EngineFixture.MakeImage(folder, "plain.png", MagickFormat.Png);
        var gamma = Target(png, "gamma");
        var set = await ProcessRunner.RunAsync(exifTool, ["-q", "-PNG:Gamma=2.2", "-o", gamma, png], TimeSpan.FromMinutes(1), Token);
        Assert.True(set.ExitCode == 0, set.StandardError);
        var cleared = Target(png, "cleared");
        await ExifTool.WriteAsync(exifTool, gamma, cleared, new MetadataEdits { RemoveAll = true }, Token);
        Assert.Equal("2.2", ValueOf(await ExifTool.ReadAsync(exifTool, cleared, Token), "PNG:Gamma"));
    }

    [Fact]
    public async Task IPTC_dates_and_times_are_written_as_ExifTool_prints_them_not_refused()
    {
        var (path, exifTool) = await PhotoAsync();
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, path, target, Setting(("IPTC:DateCreated", ["2025-02-03"]), ("IPTC:TimeCreated", ["12:00:00"])), Token);

        Assert.Equal("2025:02:03", ValueOf(await ExifTool.ReadAsync(exifTool, target, Token), "IPTC:DateCreated"));
    }

    [Fact]
    public async Task A_file_ExifTool_cant_read_is_a_failed_read_not_a_list_of_its_file_facts()
    {
        var exifTool = ExifToolOrSkip();
        var path = Path.Combine(fx.NewFolder(), "damaged.jpg");
        await File.WriteAllBytesAsync(path, [.. Enumerable.Range(0, 100).Select(i => (byte)(i * 37))], Token);

        await Assert.ThrowsAsync<IOException>(() => ExifTool.ReadAsync(exifTool, path, Token));
    }

    [Fact]
    public async Task Remove_everything_empties_a_PDF_and_takes_a_new_value_alongside()
    {
        var exifTool = ExifToolOrSkip();
        var folder = fx.NewFolder();
        var plain = Path.Combine(folder, "plain.pdf");
        using (var image = new MagickImage(MagickColors.White, 100, 100))
            image.Write(plain, MagickFormat.Pdf);
        var pdf = Path.Combine(folder, "doc.pdf");
        await ExifTool.WriteAsync(exifTool, plain, pdf, Setting(("PDF:Title", ["Old"]), ("PDF:Author", ["Kim"])), Token);
        var target = Target(pdf);

        await ExifTool.WriteAsync(exifTool, pdf, target, new MetadataEdits { RemoveAll = true, Set = { ["PDF:Title"] = ["New"] } }, Token);

        var fields = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.Equal("New", ValueOf(fields, "PDF:Title"));
        Assert.Null(ValueOf(fields, "PDF:Author"));
    }

    [Theory]
    [InlineData("100%done.jpg")]
    [InlineData("x%dy%f.jpg")]
    [InlineData("사진 €.jpg")]
    public async Task A_file_name_with_a_percent_sign_or_letters_outside_the_code_page_is_written_like_any_other(string name)
    {
        var (path, exifTool) = await PhotoAsync();
        var named = Path.Combine(Path.GetDirectoryName(path)!, name);
        File.Copy(path, named);
        // Both ways of handing over the path where both work: Perl on Windows can't take this name on its command line.
        var fallbacks = OperatingSystem.IsWindows() ? new[] { true } : [false, true];
        var argumentFile = ExifTool.PathsInArgumentFile;
        try
        {
            foreach (var inArgumentFile in fallbacks)
            {
                ExifTool.PathsInArgumentFile = inArgumentFile;
                var target = Path.Combine(Path.GetDirectoryName(path)!, $"out{inArgumentFile}%f.jpg");
                await ExifTool.WriteAsync(exifTool, named, target, new MetadataEdits { RemoveAll = true }, Token);

                // The orientation came back from the file itself, not from a file of a name made up from it.
                Assert.Equal("Rotate 90 CW", ValueOf(await ExifTool.ReadAsync(exifTool, target, Token), "IFD0:Orientation"));
                Assert.Equal("Apple", ValueOf(await ExifTool.ReadAsync(exifTool, named, Token), "IFD0:Make"));
            }
        }
        finally
        {
            ExifTool.PathsInArgumentFile = argumentFile;
        }
    }

    [Fact]
    public async Task What_ExifTool_says_it_did_is_checked_on_the_copy_not_taken_at_its_word()
    {
        if (OperatingSystem.IsWindows())
            return; // a shell script stands in for it
        var (path, exifTool) = await PhotoAsync();
        // "Done", and nothing changed: as with fields that won't come out of a file.
        var liar = Path.Combine(fx.NewFolder(), "exiftool");
        File.WriteAllText(liar, $"""
            #!/bin/sh
            case " $* " in
              *" -m "*) ;;
              *) exec "{exifTool}" "$@" ;;
            esac
            """);
        File.SetUnixFileMode(liar, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var folder = Path.GetDirectoryName(path)!;
        foreach (var edits in new[] { new MetadataEdits { RemoveAll = true }, new MetadataEdits { RemoveLocation = true }, new MetadataEdits { Remove = ["IFD0:Make"] } })
        {
            var target = Path.Combine(folder, "liar.jpg");
            var error = await Assert.ThrowsAsync<MetadataRefusedException>(() => ExifTool.WriteAsync(liar, path, target, edits, Token));

            Assert.False(File.Exists(target), error.Message);
            Assert.Empty(Directory.GetDirectories(folder));
            if (!edits.RemoveAll)
                Assert.Contains(edits.RemoveLocation ? "GPS:GPSLatitude" : "IFD0:Make", error.Message, StringComparison.Ordinal);
        }

        // And what should have stayed: everything gone, the orientation with it.
        var thorough = Path.Combine(fx.NewFolder(), "exiftool");
        File.WriteAllText(thorough, $"""
            #!/bin/sh
            case " $* " in
              *" -m "*) exec "{exifTool}" -q -overwrite_original -all= copy.jpg ;;
              *) exec "{exifTool}" "$@" ;;
            esac
            """);
        File.SetUnixFileMode(thorough, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var gone = await Assert.ThrowsAsync<MetadataRefusedException>(() =>
            ExifTool.WriteAsync(thorough, path, Path.Combine(folder, "thorough.jpg"), new MetadataEdits { RemoveAll = true }, Token));
        Assert.Contains("IFD0:Orientation", gone.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_everything_keeps_only_the_orientation_and_the_colour_profile_and_the_colours()
    {
        var exifTool = ExifToolOrSkip();
        var folder = fx.NewFolder();
        // A CMYK JPEG with a print profile: what it looks like depends on the profile and on Adobe's marker.
        var plain = Path.Combine(folder, "plain.jpg");
        using (var image = new MagickImage(new MagickColor("#3C7AC8"), 32, 24))
        {
            image.TransformColorSpace(ColorProfiles.SRGB, ColorProfiles.USWebCoatedSWOP);
            image.SetProfile(ColorProfiles.USWebCoatedSWOP);
            image.Write(plain, MagickFormat.Jpeg);
        }
        var path = Path.Combine(folder, "print.jpg");
        await ExifTool.WriteAsync(exifTool, plain, path, Setting(("IFD0:Orientation", ["Rotate 180"]), ("IFD0:Make", ["Canon"]),
            ("XMP-dc:Title", ["Proof"]), ("GPS:GPSLatitude", ["10"])), Token);
        var before = Pixels(path);
        var target = Target(path);

        await ExifTool.WriteAsync(exifTool, path, target, new MetadataEdits { RemoveAll = true }, Token);

        var fields = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.Equal("Rotate 180", ValueOf(fields, "IFD0:Orientation"));
        Assert.Contains(fields, f => f.Family == "ICC_Profile");
        Assert.DoesNotContain(fields, f => f.Family is "XMP" or "IPTC" || f.Group is "GPS" || f.Tag is "Make" or "Model");
        Assert.Equal(before, Pixels(target));
    }

    [Fact]
    public async Task PNGs_and_PDFs_get_their_fields_written_without_a_re_encode()
    {
        var exifTool = ExifToolOrSkip();
        var folder = fx.NewFolder();
        var png = EngineFixture.MakeImage(folder, "shot.png", MagickFormat.Png);
        var pngCopy = Target(png);
        await ExifTool.WriteAsync(exifTool, png, pngCopy, Setting(("XMP-dc:Title", ["Screenshot, edited"]), ("XMP-dc:Creator", ["Smith, John"])), Token);
        var pngFields = await ExifTool.ReadAsync(exifTool, pngCopy, Token);
        Assert.Equal("Screenshot, edited", ValueOf(pngFields, "XMP-dc:Title"));
        Assert.Equal(["Smith, John"], pngFields.Single(f => f.Key == "XMP-dc:Creator").Items);
        Assert.Equal(Pixels(png), Pixels(pngCopy));

        var pdf = Path.Combine(folder, "doc.pdf");
        using (var image = new MagickImage(MagickColors.White, 100, 100))
            image.Write(pdf, MagickFormat.Pdf);
        var pdfCopy = Target(pdf);
        await ExifTool.WriteAsync(exifTool, pdf, pdfCopy, Setting(("PDF:Title", ["Quarterly report"]), ("PDF:Author", ["Kim, Alex"])), Token);
        var pdfFields = await ExifTool.ReadAsync(exifTool, pdfCopy, Token);
        Assert.Equal("Quarterly report", ValueOf(pdfFields, "PDF:Title"));
        Assert.Equal("Kim, Alex", ValueOf(pdfFields, "PDF:Author"));
        // An update added at the end: what was in the file is there byte for byte.
        var pdfBytes = await File.ReadAllBytesAsync(pdf, Token);
        Assert.Equal(pdfBytes, (await File.ReadAllBytesAsync(pdfCopy, Token))[..pdfBytes.Length]);
    }

    [Fact]
    public async Task XMP_nested_in_itself_doesnt_hold_ExifTool_up()
    {
        var (_, exifTool) = await PhotoAsync();
        var xmp = System.Text.Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description rdf:about='' xmlns:a='a:'>"
            + string.Concat(Enumerable.Repeat("<a:p rdf:parseType='Resource'>", 40)) + "<a:q>x</a:q>" + string.Concat(Enumerable.Repeat("</a:p>", 40))
            + "</rdf:Description></rdf:RDF></x:xmpmeta>");
        byte[] segment = [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. xmp];
        var length = segment.Length + 2;
        var path = Path.Combine(fx.NewFolder(), "nested.jpg");
        await File.WriteAllBytesAsync(path, [0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. segment, 0xFF, 0xD9], Token);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await ExifTool.ReadAsync(exifTool, path, Token);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), clock.Elapsed.ToString());
    }

    /// <summary>One pixel and a text chunk compressed down to a thousandth of its size.</summary>
    private static byte[] PngWithComment(int megabytes, char letter = 'a')
    {
        static byte[] Chunk(string type, byte[] data)
        {
            byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
            var crc = uint.MaxValue;
            foreach (var value in typed)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? crc >> 1 ^ 0xEDB88320 : crc >> 1;
            }
            var length = new byte[4];
            var sum = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sum, ~crc);
            return [.. length, .. typed, .. sum];
        }
        using var text = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(text, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var block = new byte[1024 * 1024];
            Array.Fill(block, (byte)letter);
            for (var i = 0; i < megabytes; i++)
                zlib.Write(block);
        }
        using var pixels = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(pixels, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write([0, 0]);
        return
        [
            0x89, .. "PNG\r\n\u001a\n"u8,
            .. Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 0, 0, 0, 0]),
            .. Chunk("zTXt", [.. "Comment\0\0"u8, .. text.ToArray()]),
            .. Chunk("IDAT", pixels.ToArray()),
            .. Chunk("IEND", []),
        ];
    }

    [Fact]
    public async Task A_small_file_that_unpacks_to_far_more_text_than_a_file_holds_is_refused()
    {
        var exifTool = ExifToolOrSkip();
        var path = Path.Combine(fx.NewFolder(), "packed.png");
        await File.WriteAllBytesAsync(path, PngWithComment(100), Token);

        await Assert.ThrowsAsync<MetadataTooLargeException>(() => ExifTool.ReadAsync(exifTool, path, Token));
    }

    [Fact]
    public async Task A_read_of_a_slow_file_stops_when_cancelled()
    {
        var exifTool = ExifToolOrSkip();
        var path = Path.Combine(fx.NewFolder(), "packed.png");
        await File.WriteAllBytesAsync(path, PngWithComment(1400, '\0'), Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(300));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExifTool.ReadAsync(exifTool, path, cancel.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task A_conversion_of_a_file_over_a_reads_limits_is_refused_and_says_why()
    {
        var converter = fx.Converters.OfType<ExifToolConverter>().Single();
        Assert.SkipUnless(fx.Catalog.StatusOf(converter).IsAvailable, "Filee doesn't find this ExifTool (Settings → Engines).");
        var path = Path.Combine(fx.NewFolder(), "packed.png");
        await File.WriteAllBytesAsync(path, PngWithComment(100), Token);
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.MetadataId);
        tool.Output.Conflict = ConflictPolicy.Rename;

        var job = await fx.ConvertAsync([path], MetadataTool.ConversionOf(tool, new MetadataEdits { RemoveAll = true }, replaceOriginal: false));

        var file = Assert.Single(job.Files);
        Assert.Equal((FileState.Failed, "error.metadata_refused"), (file.State, file.ErrorKey));
    }

    [Fact]
    public async Task Remove_everything_takes_a_value_too_long_to_type_in()
    {
        var (path, exifTool) = await PhotoAsync();
        var filled = Target(path, "filled");
        await ExifTool.WriteAsync(exifTool, path, filled, Setting(("XMP-dc:Description", [new string('a', 70_000)])), Token);
        var cleared = Target(path, "cleared");

        await ExifTool.WriteAsync(exifTool, filled, cleared, new MetadataEdits { RemoveAll = true }, Token);

        Assert.Null(ValueOf(await ExifTool.ReadAsync(exifTool, cleared, Token), "XMP-dc:Description"));
    }

    [Fact]
    public async Task A_small_file_that_takes_gigabytes_and_prints_almost_nothing_is_refused()
    {
        // Zeros print as next to nothing, so only the memory limit can stop this: ExifTool peaks at 4 GB without it,
        // through the launcher and its Perl on Windows too.
        var exifTool = ExifToolOrSkip();
        var path = Path.Combine(fx.NewFolder(), "packed.png");
        await File.WriteAllBytesAsync(path, PngWithComment(1400, '\0'), Token);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<IOException>(() => ExifTool.ReadAsync(exifTool, path, Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(100), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task A_long_value_a_file_really_holds_is_read_whole()
    {
        var exifTool = ExifToolOrSkip();
        var path = Path.Combine(fx.NewFolder(), "long.png");
        await File.WriteAllBytesAsync(path, PngWithComment(4), Token);

        var fields = await ExifTool.ReadAsync(exifTool, path, Token);

        Assert.Equal(4 * 1024 * 1024, ValueOf(fields, "PNG:Comment")?.Length);
    }

    private static void MakeRunnable(string script)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>A file to read and where a script standing in for ExifTool goes: these need no ExifTool.</summary>
    private (string Path, string Spy) SpyPaths()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "photo.jpg");
        File.WriteAllText(path, "x");
        return (path, Path.Combine(folder, "exiftool"));
    }

    [Fact]
    public async Task A_read_cancelled_stops_ExifTool_and_leaves_no_folder()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "A shell script stands in for it.");
        var (path, spy) = SpyPaths();
        var seen = Path.Combine(Path.GetDirectoryName(spy)!, "seen.txt");
        File.WriteAllText(spy, $"#!/bin/sh\n{{ echo $$; pwd; }} > \"{seen}.tmp\"\nmv \"{seen}.tmp\" \"{seen}\"\nexec sleep 300\n");
        MakeRunnable(spy);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var read = ExifTool.ReadAsync(spy, path, cancel.Token);
        while (!File.Exists(seen))
            await Task.Delay(20, Token);
        var lines = await File.ReadAllLinesAsync(seen, Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.False(Directory.Exists(lines[1]));
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(int.Parse(lines[0])));
    }

    [Fact]
    public async Task A_read_stops_when_cancelled_though_a_child_holds_the_output()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "A shell script stands in for it.");
        var (path, spy) = SpyPaths();
        File.WriteAllText(spy, "#!/bin/sh\nsleep 30 &\nprintf '[{}]'\n");
        MakeRunnable(spy);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(1));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExifTool.ReadAsync(spy, path, cancel.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task ExifTool_stopped_part_way_through_printing_is_a_failed_read()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "A shell script stands in for it.");
        var (path, spy) = SpyPaths();
        File.WriteAllText(spy, "#!/bin/sh\nprintf '[{\"File:FileType\": \"PN'\n");
        MakeRunnable(spy);

        await Assert.ThrowsAsync<IOException>(() => ExifTool.ReadAsync(spy, path, Token));
    }

    [Fact]
    public async Task ExifTool_printing_without_end_is_stopped()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "A shell script stands in for it.");
        var (path, spy) = SpyPaths();
        File.WriteAllText(spy, "#!/bin/sh\nexec yes aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");
        MakeRunnable(spy);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<MetadataTooLargeException>(() => ExifTool.ReadAsync(spy, path, Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task ExifTool_filling_the_memory_is_stopped()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "A shell script stands in for it.");
        var (path, spy) = SpyPaths();
        File.WriteAllText(spy, "#!/bin/sh\nexec perl -e 'my @held; push @held, q(a) x 10_000_000 for 1..300; sleep 300'\n");
        MakeRunnable(spy);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<IOException>(() => ExifTool.ReadAsync(spy, path, Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task The_folder_a_copy_is_written_in_is_private_while_ExifTool_runs()
    {
        if (OperatingSystem.IsWindows())
            return; // a shell script stands in for it
        var (path, exifTool) = await PhotoAsync();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var folder = fx.NewFolder();
        var seen = Path.Combine(folder, "seen.txt");
        var spy = Path.Combine(folder, "exiftool");
        File.WriteAllText(spy, $$"""
            #!/bin/sh
            case " $* " in
              *" -m "*) ls -ld . > "{{seen}}" ;;
            esac
            exec "{{exifTool}}" "$@"
            """);
        File.SetUnixFileMode(spy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await ExifTool.WriteAsync(spy, path, Target(path, "private"), Setting(("IFD0:Model", ["iPhone 16"])), Token);

        Assert.StartsWith("drwx------", await File.ReadAllTextAsync(seen, Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_name_with_a_line_break_cant_add_arguments_of_its_own()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows file names can't hold a line break
        var (path, exifTool) = await PhotoAsync();
        // Were the name a line of the argument file, its second line would be an argument: here, a Make of its own.
        var tricky = Path.Combine(Path.GetDirectoryName(path)!, "photo\n-IFD0:Make=Injected\n.jpg");
        File.Copy(path, tricky);
        var target = Path.Combine(Path.GetDirectoryName(path)!, "-o\n.jpg");

        await ExifTool.WriteAsync(exifTool, tricky, target, Setting(("IFD0:Model", ["iPhone 16"])), Token);

        var fields = await ExifTool.ReadAsync(exifTool, target, Token);
        Assert.Equal("iPhone 16", ValueOf(fields, "IFD0:Model"));
        Assert.Equal("Apple", ValueOf(fields, "IFD0:Make"));
    }

    [Theory]
    [InlineData("IFD0:Make", true)]
    [InlineData("IPTC:Caption-Abstract", true)]
    [InlineData("XMP-dc:Title", true)]
    [InlineData("IFD0:Make\n-o", false)]
    [InlineData("-all", false)]
    [InlineData("IFD0:Make=x", false)]
    [InlineData("Make", false)]
    public void Only_plain_field_names_reach_the_argument_file(string key, bool accepted)
    {
        if (accepted)
            Assert.Equal(key, ExifTool.CheckedKey(key));
        else
            Assert.Throws<ArgumentException>(() => ExifTool.CheckedKey(key));
    }

    [Fact]
    public void Values_of_one_plain_line_go_in_the_argument_file_and_the_rest_through_a_file_of_their_own()
    {
        Assert.Equal("-sep\n, \n-IFD0:Make=a@b $HOME \\ c\n", ExifTool.ArgumentFile(["-sep", ", ", "-IFD0:Make=a@b $HOME \\ c"]));
        Assert.True(ExifTool.IsPlain("a@b $HOME \\ c"));
        Assert.False(ExifTool.IsPlain("two\nlines"));
        Assert.False(ExifTool.IsPlain(" leading"));
    }

    [Fact]
    public void Warnings_about_what_was_asked_are_refusals_and_the_rest_are_not()
    {
        var edits = Setting(("IPTC:ObjectName", ["x"]));
        ProcessResult Warned(string line) => new(0, "", line + "\n    1 image files created\n");

        Assert.NotNull(ExifTool.Refusals(Warned("Warning: [Minor] IPTC:ObjectName exceeds length limit (truncated) - a.jpg"), edits));
        Assert.NotNull(ExifTool.Refusals(Warned("Warning: Some character(s) could not be encoded in Latin - a.jpg"), edits));
        Assert.NotNull(ExifTool.Refusals(Warned("Warning: Tag 'IFD0:Nope' is not defined"), edits));
        Assert.Null(ExifTool.Refusals(Warned("Warning: ICC_Profile deleted. Image colors may be affected - a.jpg"), edits));
        Assert.Null(ExifTool.Refusals(Warned("Warning: IPTCDigest is not current. XMP may be out of sync - a.jpg"), edits));
        Assert.Null(ExifTool.Refusals(Warned(""), edits));
    }

    [Fact]
    public void A_long_video_gets_as_long_to_write_as_copying_it_takes()
    {
        var folder = fx.NewFolder();
        var small = Path.Combine(folder, "small.mp4");
        File.WriteAllBytes(small, new byte[1000]);
        Assert.Equal(2, ExifTool.WriteLimit(small).TotalMinutes, 0.01);
        var large = Path.Combine(folder, "large.mp4");
        using (var stream = File.Create(large))
            stream.SetLength(5_000_000_000);
        Assert.Equal(12, ExifTool.WriteLimit(large).TotalMinutes, 0.01);
    }

    [Fact]
    public void The_converter_takes_only_files_ExifTool_writes_each_into_its_own_format_and_never_an_ordinary_route()
    {
        var converter = fx.Converters.OfType<ExifToolConverter>().Single();

        Assert.All(converter.Edges, e => Assert.True(e is { Metadata: true } && e.From == e.To, $"{e.From} → {e.To}"));
        foreach (var format in new[] { "jpg", "png", "tiff", "webp", "heic", "pdf", "mp4", "mov", "m4a", "gif" })
            Assert.Contains(converter.Edges, e => e.From == format);
        // ExifTool reads these but can't write them: the window shows them read-only and no conversion is offered.
        foreach (var format in new[] { "mp3", "flac", "wav", "ogg", "mkv", "webm", "avi", "bmp", "svg" })
        {
            Assert.DoesNotContain(converter.Edges, e => e.From == format);
            Assert.False(ExifTool.Writes(format), format);
        }
        Assert.All(converter.Edges, e => Assert.NotNull(FormatRegistry.FindById(e.From)));
        // By the file's own extension: a format can have others ExifTool doesn't write.
        Assert.True(ExifTool.WritesFile("clip.f4v"));
        Assert.False(ExifTool.WritesFile("clip.flv"));
        Assert.True(ExifTool.WritesFile("PHOTO.JPG"));
        Assert.True(ExifTool.WritesFile("photo.jfif"));
        Assert.False(ExifTool.RemovesEverythingFrom("shot.CR2"));
        Assert.False(ExifTool.RemovesEverythingFrom("shot.dng"));
        Assert.True(ExifTool.RemovesEverythingFrom("shot.tif"));

        // Whatever else is installed, a copy with changes is written by ExifTool alone, and nothing else uses it.
        var planner = new RoutePlanner(fx.Converters);
        Assert.NotSame(converter, planner.Plan("jpg", "jpg")?.Steps.Single().Converter);
        Assert.DoesNotContain(planner.Plan("jpg", "png")?.Steps ?? [], s => s.Converter == converter);
        var route = planner.Plan("jpg", "jpg", metadata: true)!;
        Assert.Same(converter, route.Steps.Single().Converter);
        Assert.Null(planner.Plan("mp3", "mp3", metadata: true));
    }

    [Fact]
    public async Task A_conversion_with_changes_writes_a_copy_by_the_output_rule_and_the_original_stays_as_it_was()
    {
        var (path, _) = await PhotoAsync();
        var converter = fx.Converters.OfType<ExifToolConverter>().Single();
        Assert.SkipUnless(fx.Catalog.StatusOf(converter).IsAvailable, "Filee doesn't find this ExifTool (Settings → Engines).");
        var before = await File.ReadAllBytesAsync(path, Token);
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.MetadataId);
        var edits = Setting(("XMP-dc:Title", ["New title"]));
        edits.RemoveLocation = true;

        var job = await fx.ConvertAsync([path], MetadataTool.ConversionOf(tool, edits, replaceOriginal: false));

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var copy = Assert.Single(job.Outputs);
        Assert.Equal("photo_metadata.jpg", Path.GetFileName(copy));
        var fields = await ExifTool.ReadAsync(FindExifTool()!, copy, Token);
        Assert.Equal("New title", ValueOf(fields, "XMP-dc:Title"));
        Assert.DoesNotContain(fields, f => f.Group == "GPS");
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Token));
        Assert.Equal(Pixels(path), Pixels(copy));

        // Overwrite: an earlier copy of that name is replaced (ExifTool itself writes only new files).
        tool.Output.Conflict = ConflictPolicy.Overwrite;
        var again = await fx.ConvertAsync([path], MetadataTool.ConversionOf(tool, Setting(("XMP-dc:Title", ["Second"])), replaceOriginal: false));
        Assert.Equal(copy, Assert.Single(again.Outputs));
        // Written beside it, on its disk; nothing left there.
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(copy)!));
        Assert.Equal("Second", ValueOf(await ExifTool.ReadAsync(FindExifTool()!, copy, Token), "XMP-dc:Title"));

        // Refused: the toast says why, in the user's language, and there's no copy.
        tool.Output.Conflict = ConflictPolicy.Rename;
        var refused = await fx.ConvertAsync([path], MetadataTool.ConversionOf(tool, Setting(("ExifIFD:DateTimeOriginal", ["soon"])), replaceOriginal: false));
        var file = Assert.Single(refused.Files);
        Assert.Equal((FileState.Failed, "error.metadata_refused"), (file.State, file.ErrorKey));
        Assert.Contains("DateTimeOriginal", file.ErrorDetail, StringComparison.Ordinal);
        Assert.Empty(refused.Outputs);
    }
}
