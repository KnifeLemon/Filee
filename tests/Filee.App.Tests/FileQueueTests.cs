// The Home page's queue (multi-file conversion): one format at a time, in the order the user puts it in, and a merge that
// keeps that order instead of sorting by name.

using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Formats;
using Xunit;

namespace Filee.App.Tests;

public class FileQueueTests
{
    [Fact]
    public void TheFirstFileSetsTheTypeAndOtherFormatsAreRefused()
    {
        var queue = new FileQueue();

        var refused = queue.Add(["/docs/one.pdf", "/docs/letter.docx", "/docs/three.pdf", "/docs/photo.png"]);

        Assert.Equal(["/docs/one.pdf", "/docs/three.pdf"], queue.Paths);
        Assert.Equal(FormatCategory.Pdf, queue.Format?.Category);
        Assert.Equal<(string, QueueRefusal)[]>(
            [("/docs/letter.docx", QueueRefusal.OtherFormat), ("/docs/photo.png", QueueRefusal.OtherFormat)],
            refused.Select(r => (r.Path, r.Reason)).ToArray());
    }

    [Fact]
    public void FilesOfAFormatFileeDoesNotKnowAreRefusedAsUnsupported()
    {
        var queue = new FileQueue();

        var refused = queue.Add(["/docs/notes.unknownext"]);

        Assert.Empty(queue.Paths);
        Assert.Null(queue.Format);
        Assert.Equal(QueueRefusal.Unsupported, Assert.Single(refused).Reason);
    }

    [Fact]
    public void AFileAddedTwiceIsKeptOnce()
    {
        var queue = new FileQueue();

        queue.Add(["/docs/one.pdf"]);
        var refused = queue.Add(["/docs/one.pdf", "/docs/two.pdf"]);

        Assert.Equal(["/docs/one.pdf", "/docs/two.pdf"], queue.Paths);
        Assert.Empty(refused);
    }

    [Fact]
    public void MovingAFileChangesTheOrder()
    {
        var queue = new FileQueue();
        queue.Add(["/docs/a.pdf", "/docs/b.pdf", "/docs/c.pdf"]);

        queue.Move(2, 0);

        Assert.Equal(["/docs/c.pdf", "/docs/a.pdf", "/docs/b.pdf"], queue.Paths);
    }

    [Fact]
    public void RemovingTheLastFileLetsAnotherTypeStart()
    {
        var queue = new FileQueue();
        queue.Add(["/docs/a.pdf"]);

        Assert.True(queue.Remove("/docs/a.pdf"));
        Assert.Null(queue.Format);
        Assert.Empty(queue.Add(["/photos/b.png"]));
        Assert.Equal(["/photos/b.png"], queue.Paths);
    }

    [Fact]
    public void ClearEmptiesTheQueueAndItsType()
    {
        var queue = new FileQueue();
        queue.Add(["/docs/a.pdf"]);

        queue.Clear();

        Assert.Empty(queue.Paths);
        Assert.Null(queue.Format);
    }

    [Fact]
    public void AMergeKeepsTheOrderTheFilesCameIn()
    {
        var files = new[] { "/docs/page 10.pdf", "/docs/page 2.pdf", "/docs/page 1.pdf" };

        var kept = new MergeOrderViewModel(files, keepOrder: true);
        var sorted = new MergeOrderViewModel(files);

        Assert.Equal(files, kept.OrderedFiles);
        Assert.Equal(["/docs/page 1.pdf", "/docs/page 2.pdf", "/docs/page 10.pdf"], sorted.OrderedFiles);
    }
}
