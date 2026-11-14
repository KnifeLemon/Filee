// Split PDF (#1259): which of the chosen pages go into which file (one page each, so many pages each, or a number of
// parts of the same length) and the name each file ends with.

using Filee.Core.Presets;

namespace Filee.Core.Conversion;

public static class PageGroups
{
    /// <summary>The highest number of pages per file or of parts the window and the editor take.</summary>
    public const int MaxSize = 9999;

    /// <summary>
    /// The pages (zero-based, as <see cref="PageRange.Parse"/> gives them) in the files a split writes, in order:
    /// <see cref="PdfOptions.SplitParts"/> files of the same length when it is set, the first ones a page longer when
    /// the pages don't divide evenly (10 pages in thirds are 4, 3 and 3); else <see cref="PdfOptions.SplitEvery"/>
    /// pages to a file, the last one taking what is left; else one page per file.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Of(IReadOnlyList<int> pages, PdfOptions options)
    {
        if (pages.Count == 0)
            return [];
        if (options.SplitParts > 1)
        {
            // More parts than pages: a page each, not empty files.
            var parts = Math.Min(options.SplitParts, pages.Count);
            var groups = new List<IReadOnlyList<int>>(parts);
            var start = 0;
            for (var i = 0; i < parts; i++)
            {
                var length = pages.Count / parts + (i < pages.Count % parts ? 1 : 0);
                groups.Add([.. pages.Skip(start).Take(length)]);
                start += length;
            }
            return groups;
        }
        return [.. pages.Chunk(Math.Max(1, options.SplitEvery))];
    }

    /// <summary>
    /// What a file of the split ends with: <c>_p5</c> for one page, as before; <c>_p51-100</c> for several, its first
    /// and last page.
    /// </summary>
    public static string Suffix(IReadOnlyList<int> group) =>
        group.Count == 1 ? $"_p{group[0] + 1}" : $"_p{group[0] + 1}-{group[^1] + 1}";
}
