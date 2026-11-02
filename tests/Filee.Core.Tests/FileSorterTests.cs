// The orders files merge in: by name the way people count, by the dates on disk, and what happens without a date.

using Filee.Core.Platform;

namespace Filee.Core.Tests;

public class FileSorterTests
{
    private static readonly DateTime Early = new(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2024, 1, 1, 9, 30, 0, DateTimeKind.Utc); // same day: the time decides
    private static readonly DateTime Latest = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string[] Sorted(FileSortOrder order, params FileStamp[] files) =>
        FileSorter.Sort(files, file => file, order).Select(file => file.FilePath).ToArray();

    [Fact]
    public void Names_sort_the_way_people_count_in_both_directions()
    {
        FileStamp[] files = [new("page 10.pdf"), new("Page 2.pdf"), new("page 1.pdf")];
        Assert.Equal(["page 1.pdf", "Page 2.pdf", "page 10.pdf"], Sorted(FileSortOrder.NameAscending, files));
        Assert.Equal(["page 10.pdf", "Page 2.pdf", "page 1.pdf"], Sorted(FileSortOrder.NameDescending, files));
    }

    [Fact]
    public void Equal_names_in_different_folders_sort_by_folder()
    {
        string a = Path.Combine("a", "x.pdf"), b = Path.Combine("b", "x.pdf");
        Assert.Equal([a, b], Sorted(FileSortOrder.NameAscending, new(b), new(a)));
        Assert.Equal([b, a], Sorted(FileSortOrder.NameDescending, new(a), new(b)));
    }

    [Fact]
    public void Each_date_sorts_on_its_own_down_to_the_time_of_day()
    {
        // The two dates disagree on purpose: a copied file is often "created" after it was last modified.
        FileStamp[] files =
        [
            new("a.pdf", Modified: Latest, Created: Early),
            new("b.pdf", Modified: Early, Created: Later),
            new("c.pdf", Modified: Later, Created: Latest),
        ];
        Assert.Equal(["b.pdf", "c.pdf", "a.pdf"], Sorted(FileSortOrder.ModifiedAscending, files));
        Assert.Equal(["a.pdf", "c.pdf", "b.pdf"], Sorted(FileSortOrder.ModifiedDescending, files));
        Assert.Equal(["a.pdf", "b.pdf", "c.pdf"], Sorted(FileSortOrder.CreatedAscending, files));
        Assert.Equal(["c.pdf", "b.pdf", "a.pdf"], Sorted(FileSortOrder.CreatedDescending, files));
    }

    [Theory]
    [InlineData(FileSortOrder.ModifiedAscending)]
    [InlineData(FileSortOrder.ModifiedDescending)]
    public void Equal_dates_sort_by_name_and_files_without_a_date_come_last(FileSortOrder order)
    {
        FileStamp[] files = [new("z unknown.pdf"), new("page 10.pdf", Early), new("a unknown.pdf"), new("page 2.pdf", Early)];
        Assert.Equal(["page 2.pdf", "page 10.pdf", "a unknown.pdf", "z unknown.pdf"], Sorted(order, files));
    }

    [Fact]
    public void Reads_the_dates_on_disk_and_none_for_a_missing_file()
    {
        var folder = Directory.CreateTempSubdirectory("filee-sort-").FullName;
        try
        {
            var path = Path.Combine(folder, "real.pdf");
            File.WriteAllBytes(path, [1]);
            File.SetLastWriteTimeUtc(path, Later);
            File.SetCreationTimeUtc(path, Early);

            var stamp = FileStamp.Read(path);
            Assert.Equal(Later, stamp.Modified);
            if (OperatingSystem.IsWindows()) // Linux can't set a file's creation time
                Assert.Equal(Early, stamp.Created);

            var missing = FileStamp.Read(Path.Combine(folder, "missing.pdf"));
            Assert.Null(missing.Modified);
            Assert.Null(missing.Created);
        }
        finally { Directory.Delete(folder, true); }
    }
}
