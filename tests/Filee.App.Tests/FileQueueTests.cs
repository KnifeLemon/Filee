// The Home page's queue (multi-file conversion): any files, in the order the user puts them in, and a merge that keeps
// that order instead of sorting by name.

using Filee.App.Services;
using Filee.App.ViewModels;
using Xunit;

namespace Filee.App.Tests;

public class FileQueueTests
{
    [Fact]
    public void FilesOfAnyTypeJoinTheQueueInTheOrderAdded()
    {
        var queue = new FileQueue();

        queue.Add(["/docs/one.pdf", "/docs/letter.docx", "/photos/photo.png", "/docs/notes.unknownext"]);

        Assert.Equal(["/docs/one.pdf", "/docs/letter.docx", "/photos/photo.png", "/docs/notes.unknownext"], queue.Paths);
    }

    [Fact]
    public void AFileAddedTwiceIsKeptOnce()
    {
        var queue = new FileQueue();

        queue.Add(["/docs/one.pdf"]);
        queue.Add(["/docs/one.pdf", "/docs/two.pdf"]);

        Assert.Equal(["/docs/one.pdf", "/docs/two.pdf"], queue.Paths);
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
    public void RemovingAFileKeepsTheOthersInOrder()
    {
        var queue = new FileQueue();
        queue.Add(["/docs/a.pdf", "/photos/b.png", "/docs/c.pdf"]);

        Assert.True(queue.Remove("/photos/b.png"));

        Assert.Equal(["/docs/a.pdf", "/docs/c.pdf"], queue.Paths);
        Assert.False(queue.Remove("/photos/b.png"));
    }

    [Fact]
    public void ClearEmptiesTheQueue()
    {
        var queue = new FileQueue();
        queue.Add(["/docs/a.pdf"]);

        queue.Clear();

        Assert.Empty(queue.Paths);
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
