// "Size limit" (#553): only the result has to fit, so only the step that writes it sees the limit; and the
// ready-made presets for where a file goes are offered by Add only, never put in the library on their own.

using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class SizeLimitTests
{
    [Fact]
    public async Task Only_the_step_that_writes_the_result_gets_the_size_limit()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        var first = new FakeConverter("magick", new ConversionEdge("jpg", "png"));
        var second = new FakeConverter("other", new ConversionEdge("png", "webp"));
        await using var queue = new JobQueue(new ConverterCatalog([first, second]));
        var preset = new Preset { TargetFormat = "webp", Image = { MaxSizeMb = 2 }, Media = { MaxSizeMb = 5 } };

        await queue.RunAsync(new ConversionJob([photo], preset, "WEBP"));

        var middle = Assert.Single(first.Presets);
        Assert.Equal((0.0, 0.0), (middle.Image.MaxSizeMb, middle.Media.MaxSizeMb));
        var last = Assert.Single(second.Presets);
        Assert.Equal((2.0, 5.0), (last.Image.MaxSizeMb, last.Media.MaxSizeMb));
        Assert.Equal(2, preset.Image.MaxSizeMb); // the user's preset itself is never changed
    }

    [Fact]
    public async Task A_result_no_engine_fitted_is_refused_rather_than_handed_over_too_large()
    {
        // The fake engine writes its result as it is (like HEIC, a CAD render or an animation): the queue checks it.
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var job = new ConversionJob([photo], new Preset { TargetFormat = "png", Image = { MaxSizeMb = 0.000001 } }, "PNG");

        await queue.RunAsync(job);

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("error.over_size_limit", file.ErrorKey);
        Assert.Empty(job.Outputs);
        Assert.Equal([photo], Directory.GetFiles(dir.Path)); // nothing too large is left behind
    }

    [Fact]
    public async Task A_result_within_the_limit_or_without_one_is_kept()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));

        var fits = new ConversionJob([photo], new Preset { TargetFormat = "png", Image = { MaxSizeMb = 1 } }, "PNG");
        await queue.RunAsync(fits);
        // A video limit says nothing about a picture.
        var other = new ConversionJob([photo], new Preset { TargetFormat = "png", Media = { MaxSizeMb = 0.000001 }, Output = { FileNamePattern = "{name}_2" } }, "PNG");
        await queue.RunAsync(other);

        Assert.Equal(JobState.Completed, fits.State);
        Assert.Equal(JobState.Completed, other.State);
    }

    [Fact]
    public async Task A_minimum_alone_is_also_only_for_the_step_that_writes_the_result()
    {
        // No maximum at all: the minimum on its own must be taken off the steps on the way (#662).
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        var first = new FakeConverter("magick", new ConversionEdge("jpg", "png"));
        var second = new FakeConverter("other", new ConversionEdge("png", "webp"));
        await using var queue = new JobQueue(new ConverterCatalog([first, second]));

        await queue.RunAsync(new ConversionJob([photo], new Preset { TargetFormat = "webp", Image = { MinSizeMb = 0.06 } }, "WEBP"));

        Assert.Equal(0, Assert.Single(first.Presets).Image.MinSizeMb);
        Assert.Equal(0.06, Assert.Single(second.Presets).Image.MinSizeMb);
    }

    [Theory]
    [InlineData(0.001, true)]    // 1,000 bytes: the fake result is a few dozen
    [InlineData(0.00001, false)] // 10 bytes
    [InlineData(0, false)]       // no minimum
    public async Task A_picture_under_the_minimum_is_kept_with_a_notice(double minimum, bool under)
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var job = new ConversionJob([photo], new Preset { TargetFormat = "png", Image = { MinSizeMb = minimum } }, "PNG");

        await queue.RunAsync(job);

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Done, file.State);
        Assert.Single(file.Outputs);
        Assert.Equal(under, file.UnderMinimumSize);
        Assert.Equal(under, file.HasNotice);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Under_save_as_zip_the_pictures_are_measured_not_the_zip(int pictures)
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        var big = new string('a', 5_000); // packs into a ZIP far under 4,000 bytes
        var converter = new FakeConverter("magick", new ConversionEdge("jpg", "png")) { OutputsPerInput = pictures, Content = _ => big };
        await using var queue = new JobQueue(new ConverterCatalog([converter]));
        var preset = new Preset { TargetFormat = "png", Image = { MinSizeMb = 0.004 }, Output = { ZipResult = true } };

        var fits = new ConversionJob([photo], preset, "PNG");
        await queue.RunAsync(fits);

        var zip = Assert.Single(fits.Files[0].Outputs);
        Assert.EndsWith(".zip", zip);
        Assert.True(new FileInfo(zip).Length < 4_000);
        Assert.False(fits.Files[0].UnderMinimumSize);

        // One picture short (the last one) is enough for the notice.
        converter.Content = i => i == pictures - 1 ? "short" : big;
        preset.Output.FileNamePattern = "{name}_2";
        var shortJob = new ConversionJob([photo], preset, "PNG");
        await queue.RunAsync(shortJob);

        Assert.Equal(FileState.Done, shortJob.Files[0].State);
        Assert.True(shortJob.Files[0].UnderMinimumSize);
    }

    [Fact]
    public async Task One_page_under_the_minimum_is_enough_for_the_notice()
    {
        using var dir = new TempDir();
        var scan = dir.File("scan.pdf");
        var converter = new FakeConverter("pdfium", new ConversionEdge("pdf", "png")) { OutputsPerInput = 3, Content = i => i == 1 ? "short" : new string('a', 2_000) };
        await using var queue = new JobQueue(new ConverterCatalog([converter]));
        var job = new ConversionJob([scan], new Preset { TargetFormat = "png", Image = { MinSizeMb = 0.001 } }, "PNG");

        await queue.RunAsync(job);

        Assert.Equal(3, job.Files[0].Outputs.Count);
        Assert.True(job.Files[0].UnderMinimumSize);
    }

    [Fact]
    public async Task The_original_kept_as_it_was_smaller_still_tells_that_it_is_under_the_minimum()
    {
        using var dir = new TempDir();
        var tiny = dir.File("tiny.jpg", "x");
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == "compress-image").Clone();
        preset.Image.MinSizeMb = 0.0001; // 100 bytes
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "jpg"))]));
        var job = new ConversionJob([tiny], preset, "JPG");

        await queue.RunAsync(job);

        var file = Assert.Single(job.Files);
        Assert.Equal("notice.pdf_not_smaller", file.NoticeKey);
        Assert.True(file.UnderMinimumSize);
        Assert.Equal("notice.with_under_min_size(notice.pdf_not_smaller|notice.under_min_size)", new KeyLocalizer().Notice(file));
    }

    [Fact]
    public async Task A_multi_page_tiff_under_the_minimum_gets_the_notice_on_the_first_file()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        var other = dir.File("other.jpg");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "tiff"))]),
            tiffMerger: new FakeTiffMerger());
        var job = new ConversionJob([photo, other], new Preset { TargetFormat = "tiff", Image = { MultiPageTiff = true, MinSizeMb = 0.001 } }, "TIFF");

        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Single(job.Files[0].Outputs);
        Assert.True(job.Files[0].UnderMinimumSize);
        Assert.False(job.Files[1].UnderMinimumSize);
    }

    [Fact]
    public async Task A_folder_result_is_not_measured()
    {
        // Named like a picture, but a folder (video frames): left alone, not failed.
        using var dir = new TempDir();
        var clip = dir.File("clip.mp4");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("ffmpeg", new ConversionEdge("mp4", "jpg")) { OutputIsFolder = true }]));
        var job = new ConversionJob([clip], new Preset { TargetFormat = "jpg", Image = { MinSizeMb = 0.001 } }, "JPG");

        await queue.RunAsync(job);

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Done, file.State);
        Assert.True(Directory.Exists(Assert.Single(file.Outputs)));
        Assert.False(file.UnderMinimumSize);
    }

    [Fact]
    public void A_picture_limit_under_a_megabyte_reads_in_kilobytes_and_a_minimum_counts_every_byte()
    {
        Assert.EndsWith("KB", SizeLimit.Describe(61_200, 50_000, image: true));
        Assert.EndsWith("MB", SizeLimit.Describe(2_500_000, 2_000_000, image: true));
        Assert.EndsWith("MB", SizeLimit.Describe(600_000, 500_000)); // a video's limit stays in MB

        Assert.Equal(1_001_000, SizeLimit.MinimumBytes(1.001)); // truncated, it would be 1,000,999
        Assert.Equal([50_000L, 999_000L, 4_096_000_000L], new[] { 0.05, 0.999, 4096 }.Select(SizeLimit.MinimumBytes));

        using var dir = new TempDir();
        var preset = new Preset { Image = { MinSizeMb = 1.001 } };
        bool Under(int bytes) => SizeLimit.IsUnderMinimum(preset, [dir.File($"{bytes}.png", new string('a', bytes))]);
        Assert.Equal((true, false, false), (Under(1_000_999), Under(1_001_000), Under(1_001_001)));
    }

    [Fact]
    public void A_notice_shows_the_minimum_alone_or_beside_the_other_note()
    {
        var loc = new KeyLocalizer();

        Assert.Equal("notice.under_min_size", loc.Notice(new FileResult { SourcePath = "a.png", UnderMinimumSize = true }));
        Assert.Equal("", loc.Notice(new FileResult { SourcePath = "a.png" }));
        Assert.Equal("notice.pdf_not_smaller", loc.Notice(new FileResult { SourcePath = "a.png", NoticeKey = "notice.pdf_not_smaller" }));
        Assert.Equal("notice.with_under_min_size(notice.original_kept (trash.cannot_place)|notice.under_min_size)",
            loc.Notice(new FileResult { SourcePath = "a.png", NoticeKey = "notice.original_kept", NoticeReasonKey = "trash.cannot_place", UnderMinimumSize = true }));
    }

    /// <summary>Gives keys back as they are, so a test sees which sentences went into a notice.</summary>
    private sealed class KeyLocalizer : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => key;
        public string Format(string key, params object[] args) => $"{key}({string.Join("|", args)})";
        public event EventHandler? LanguageChanged { add { } remove { } }
    }

    [Fact]
    public void The_presets_for_where_a_file_goes_are_offered_but_not_installed()
    {
        var places = BuiltInData.PlacePresets();
        var library = BuiltInData.CreatePresets().Select(p => p.Id).ToHashSet();

        Assert.Equal(["for-email", "for-discord", "for-whatsapp", "for-instagram"], places.Select(p => p.Id));
        Assert.DoesNotContain(places, p => library.Contains(p.Id));
        Assert.All(places.Where(p => p.TargetFormat == "mp4"), p => Assert.True(p.Media.MaxSizeMb > 0));
        Assert.Equal(720, places.Single(p => p.Id == "for-whatsapp").Media.MaxHeight);
        Assert.Equal((ResizeMode.Width, 1080), places.Single(p => p.Id == "for-instagram").Image is var i ? (i.Resize, i.Width) : default);
        // A new list each time: adding one never changes the next.
        Assert.NotSame(places[0], BuiltInData.PlacePresets()[0]);
    }
}
