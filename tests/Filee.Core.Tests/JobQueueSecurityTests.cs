// Settings → Security and limits in the queue: documents with active content before a step through an office suite
// (ask once per job, convert, skip, cancel, refuse; never ask an unattended job), and the step time limit for built-in
// engines, which engines with a time limit of their own don't get.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class JobQueueSecurityTests
{
    private static readonly Preset ToPdf = new() { TargetFormat = "pdf" };

    [Fact]
    public async Task Refuse_fails_documents_with_active_content_and_converts_the_others()
    {
        using var dir = new TempDir();
        var macro = dir.File("macro.docx");
        var clean = dir.File("clean.docx");
        await using var queue = Queue(new FakeScanner(), ActiveContentPolicy.Refuse);

        var job = await Run(queue, [macro, clean]);

        Assert.Equal(FileState.Failed, File(job, macro).State);
        Assert.Equal("error.active_content_refused", File(job, macro).ErrorKey);
        Assert.Equal(FileState.Done, File(job, clean).State);
        Assert.Equal(JobState.CompletedWithErrors, job.State);
    }

    [Fact]
    public async Task Ask_asks_once_for_the_whole_job_and_trust_converts()
    {
        using var dir = new TempDir();
        var first = dir.File("macro-1.docx");
        var second = dir.File("macro-2.docx");
        var asked = new List<int>();
        await using var queue = Queue(new FakeScanner(), ActiveContentPolicy.Ask, (_, reports, _) =>
        {
            asked.Add(reports.Count);
            return Task.FromResult(ActiveContentDecision.Trust);
        });

        var job = await Run(queue, [first, second]);

        Assert.Equal([2], asked);
        Assert.All(job.Files, f => Assert.Equal(FileState.Done, f.State));
        Assert.All(job.Files, f => Assert.Equal("notice.active_content", f.NoticeKey));
        Assert.All(job.Files, f => Assert.Contains(f.ActiveContent, x => x.Kind == ActiveContentKind.AutoRunMacro));
    }

    [Fact]
    public async Task Skip_leaves_the_documents_out()
    {
        using var dir = new TempDir();
        var macro = dir.File("macro.docx");
        var clean = dir.File("clean.docx");
        await using var queue = Queue(new FakeScanner(), ActiveContentPolicy.Ask, (_, _, _) => Task.FromResult(ActiveContentDecision.Skip));

        var job = await Run(queue, [macro, clean]);

        Assert.Equal(FileState.Skipped, File(job, macro).State);
        Assert.Equal("notice.active_content_skipped", File(job, macro).NoticeKey);
        Assert.False(System.IO.File.Exists(Path.Combine(dir.Path, "macro.pdf")));
        Assert.Equal(FileState.Done, File(job, clean).State);
        Assert.Equal(JobState.Completed, job.State);
    }

    [Fact]
    public async Task Cancel_cancels_the_job()
    {
        using var dir = new TempDir();
        await using var queue = Queue(new FakeScanner(), ActiveContentPolicy.Ask, (_, _, _) => Task.FromResult(ActiveContentDecision.Cancel));

        var job = await Run(queue, [dir.File("macro.docx"), dir.File("clean.docx")]);

        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Empty(job.Outputs);
    }

    [Fact]
    public async Task A_prompt_that_fails_leaves_the_documents_out()
    {
        using var dir = new TempDir();
        await using var queue = Queue(new FakeScanner(), ActiveContentPolicy.Ask,
            (_, _, _) => throw new InvalidOperationException("no window"));

        var job = await Run(queue, [dir.File("macro.docx")]);

        Assert.Equal(FileState.Skipped, job.Files.Single().State);
    }

    [Fact]
    public async Task An_unattended_job_never_asks()
    {
        using var dir = new TempDir();
        var asked = 0;
        await using var queue = Queue(new FakeScanner(), ActiveContentPolicy.Ask, (_, _, _) =>
        {
            asked++;
            return Task.FromResult(ActiveContentDecision.Cancel);
        });

        var job = new ConversionJob([dir.File("macro.docx")], ToPdf, "Test", unattended: true);
        await queue.RunAsync(job);

        Assert.Equal(0, asked);
        Assert.Equal(FileState.Done, job.Files.Single().State);
        Assert.Equal("notice.active_content", job.Files.Single().NoticeKey);
    }

    [Fact]
    public async Task A_route_without_an_office_suite_is_not_checked()
    {
        using var dir = new TempDir();
        var scanner = new FakeScanner();
        var builtIn = new FakeConverter("hwpx-writer", new ConversionEdge("docx", "pdf"));
        await using var queue = new JobQueue(new ConverterCatalog([builtIn]))
        {
            ActiveContentScanner = scanner,
            ActiveContentPolicy = ActiveContentPolicy.Refuse,
        };

        var job = await Run(queue, [dir.File("macro.docx")]);

        Assert.Equal(0, scanner.Scans);
        Assert.Equal(FileState.Done, job.Files.Single().State);
    }

    [Fact]
    public async Task The_step_time_limit_stops_a_stuck_built_in_engine()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([new StuckConverter("magick", new ConversionEdge("jpg", "png"))]))
        {
            StepTimeout = TimeSpan.FromMilliseconds(300),
        };
        var clock = Stopwatch.StartNew();

        var job = await Run(queue, [dir.File("a.jpg")], new Preset { TargetFormat = "png" });

        Assert.Equal(FileState.Failed, job.Files.Single().State);
        Assert.Equal("error.conversion_timeout", job.Files.Single().ErrorKey);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Engines_with_their_own_time_limit_keep_it()
    {
        using var dir = new TempDir();
        var external = new SlowExternalConverter("ffmpeg", TimeSpan.FromMilliseconds(600), new ConversionEdge("mp4", "webm"));
        await using var queue = new JobQueue(new ConverterCatalog([external])) { StepTimeout = TimeSpan.FromMilliseconds(100) };

        var job = await Run(queue, [dir.File("a.mp4")], new Preset { TargetFormat = "webm" });

        Assert.Equal(FileState.Done, job.Files.Single().State);
    }

    [Fact]
    public void Parallel_files_is_at_least_one()
    {
        var queue = new JobQueue(new ConverterCatalog([])) { ParallelFiles = 0 };
        Assert.Equal(1, queue.ParallelFiles);
    }

    private static JobQueue Queue(IActiveContentScanner scanner, ActiveContentPolicy policy, ActiveContentPrompt? prompt = null) =>
        new(new ConverterCatalog([new OfficeSuiteConverter(new ConversionEdge("docx", "pdf"))]))
        {
            ActiveContentScanner = scanner,
            ActiveContentPolicy = policy,
            ActiveContentPrompt = prompt,
        };

    private static async Task<ConversionJob> Run(JobQueue queue, IReadOnlyList<string> sources, Preset? preset = null)
    {
        var job = new ConversionJob(sources, preset ?? ToPdf, "Test");
        await queue.RunAsync(job);
        return job;
    }

    private static FileResult File(ConversionJob job, string source) => job.Files.Single(f => f.SourcePath == source);

    /// <summary>Finds an auto-run macro in every file whose name contains "macro".</summary>
    private sealed class FakeScanner : IActiveContentScanner
    {
        private int _scans;
        public int Scans => _scans;

        public ActiveContentReport? Scan(string path, string formatId)
        {
            Interlocked.Increment(ref _scans);
            return Path.GetFileName(path).Contains("macro", StringComparison.Ordinal)
                ? new ActiveContentReport(path, [new(ActiveContentKind.Macro, "Module1"), new(ActiveContentKind.AutoRunMacro, "AutoOpen", "Module1")])
                : null;
        }
    }

    /// <summary>An office suite (LibreOffice) for the queue.</summary>
    private sealed class OfficeSuiteConverter(params ConversionEdge[] edges) : IConverter, IOpensDocumentsInOfficeSuite, IHasOwnTimeout
    {
        private readonly FakeConverter _writer = new("libreoffice", edges);
        public string Id => "libreoffice";
        public string DisplayName => "LibreOffice";
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 0;
        public EngineStatus GetStatus() => EngineStatus.Available();

        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            _writer.ConvertAsync(step, progress, cancellationToken);
    }

    /// <summary>A built-in engine stuck on a file: it never looks at the token.</summary>
    private sealed class StuckConverter(string id, params ConversionEdge[] edges) : IConverter
    {
        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 0;
        public EngineStatus GetStatus() => EngineStatus.Available();

        public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
            return [];
        }
    }

    /// <summary>An engine that runs another program under its own time limit, and takes a while.</summary>
    private sealed class SlowExternalConverter(string id, TimeSpan duration, params ConversionEdge[] edges) : IConverter, IHasOwnTimeout
    {
        private readonly FakeConverter _writer = new(id, edges);
        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 0;
        public EngineStatus GetStatus() => EngineStatus.Available();

        public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            await Task.Delay(duration, cancellationToken);
            return await _writer.ConvertAsync(step, progress, cancellationToken);
        }
    }
}
