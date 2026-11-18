// What an engine left out of a document (charts, embedded objects, vertical text …) reaches the file's result and is
// shown with its other notes: each kind once with its count ("1 chart left out" from the _one key, "2 charts …" from
// the key with the number), at most a few kinds, then "and more".

using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.Core.Tests;

public sealed class LeftOutNoticeTests
{
    /// <summary>Translations that show which key and which arguments made the text.</summary>
    private sealed class KeyLocalizer : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => key == "notice.renderer_left_out.separator" ? ", " : key;
        public string Format(string key, params object[] args) => $"{key}({string.Join("|", args)})";
        public event EventHandler? LanguageChanged { add { } remove { } }
    }

    private static readonly LeftOutNotice Charts = new("notice.renderer_left_out",
        [new LeftOutPart("notice.renderer_left_out.charts", 2), new LeftOutPart("notice.renderer_left_out.objects", 1), new LeftOutPart("notice.renderer_left_out.vertical_text", 0)]);

    [Fact]
    public async Task The_engines_note_reaches_the_files_result()
    {
        using var dir = new TempDir();
        var source = dir.File("report.docx");
        var converter = new FakeConverter("layout", new ConversionEdge("docx", "pdf")) { LeftOut = Charts };
        await using var queue = new JobQueue(new ConverterCatalog([converter]));
        var job = new ConversionJob([source], new Preset { TargetFormat = "pdf" }, "PDF");

        await queue.RunAsync(job);

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Done, file.State);
        Assert.Same(Charts, file.LeftOut);
        Assert.True(file.HasNotice);
        Assert.Equal("notice.renderer_left_out(notice.renderer_left_out.charts(2), notice.renderer_left_out.objects_one, notice.renderer_left_out.vertical_text)",
            new KeyLocalizer().Notice(file));
    }

    [Fact]
    public async Task A_document_drawn_whole_has_no_note()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("layout", new ConversionEdge("docx", "pdf"))]));
        var job = new ConversionJob([dir.File("plain.docx")], new Preset { TargetFormat = "pdf" }, "PDF");

        await queue.RunAsync(job);

        Assert.Null(job.Files[0].LeftOut);
        Assert.False(job.Files[0].HasNotice);
        Assert.Equal("", new KeyLocalizer().Notice(job.Files[0]));
    }

    [Fact]
    public void It_follows_another_notice_and_the_list_is_capped()
    {
        var file = new FileResult { SourcePath = "report.docx", NoticeKey = "notice.macros_not_run", LeftOut = Charts };
        Assert.Equal("notice.with_under_min_size(notice.macros_not_run|notice.renderer_left_out(notice.renderer_left_out.charts(2), " +
                     "notice.renderer_left_out.objects_one, notice.renderer_left_out.vertical_text))", new KeyLocalizer().Notice(file));

        var many = new LeftOutNotice("k", [.. Enumerable.Range(1, LocalizerExtensions.MaxLeftOutParts + 2).Select(i => new LeftOutPart($"p{i}", i + 1))]);
        Assert.Equal("k(p1(2), p2(3), p3(4), p4(5), notice.renderer_left_out.more)", new KeyLocalizer().LeftOut(many));
    }
}
