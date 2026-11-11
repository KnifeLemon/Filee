// "Choose pages" (#981): the pages of one PDF dropped on a preset that asks for them (PdfOptions.AskPageRange), and for
// a PDF target whether they become one PDF, one file per page or several pages per file (#1259). The range is checked
// as it is typed.

using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

/// <summary>
/// What the user chose: the range as it is used (empty for every page), split into files or not, and with a split the
/// pages per file or the equal parts (0 for one page per file; PdfOptions.SplitEvery, SplitParts).
/// </summary>
public sealed record PageChoice(string Range, bool SplitPages, int SplitEvery = 0, int SplitParts = 0);

/// <summary>What a PDF → PDF makes of its pages: one PDF, one file per page, or several pages per file (#1259).</summary>
public enum PageOutput
{
    OnePdf,
    EachPage,
    Groups,
}

/// <summary>With <see cref="PageOutput.Groups"/>: the number is the pages per file, or the parts of the same length.</summary>
public enum PageGroupKind
{
    PagesPerFile,
    EqualParts,
}

/// <summary>The split as PdfOptions keeps it, read for the window and the preset editor and written back.</summary>
public static class PageOutputs
{
    /// <summary>The number shown when the preset has none yet: halves, or two pages per file.</summary>
    public const int DefaultGroupSize = 2;

    public static PageOutput Of(PdfOptions pdf) =>
        !pdf.SplitPages ? PageOutput.OnePdf
        : pdf.SplitParts > 1 || pdf.SplitEvery > 1 ? PageOutput.Groups
        : PageOutput.EachPage;

    /// <summary>The kind of group the preset has, also while it makes one PDF (the numbers are kept then).</summary>
    public static PageGroupKind KindOf(PdfOptions pdf) => pdf.SplitParts > 1 ? PageGroupKind.EqualParts : PageGroupKind.PagesPerFile;

    public static int SizeOf(PdfOptions pdf) =>
        pdf.SplitParts > 1 ? pdf.SplitParts : pdf.SplitEvery > 1 ? pdf.SplitEvery : DefaultGroupSize;

    /// <summary>PdfOptions.SplitPages, SplitEvery and SplitParts for the choice; the numbers are 0 where they don't apply.</summary>
    public static (bool Split, int Every, int Parts) Settings(PageOutput output, PageGroupKind kind, int size)
    {
        size = Math.Clamp(size, 2, PageGroups.MaxSize);
        return output switch
        {
            PageOutput.OnePdf => (false, 0, 0),
            PageOutput.Groups when kind == PageGroupKind.EqualParts => (true, 0, size),
            PageOutput.Groups => (true, size, 0),
            _ => (true, 0, 0),
        };
    }

    public static IReadOnlyList<Choice<PageGroupKind>> Kinds(ILocalizer loc) =>
    [
        new(PageGroupKind.PagesPerFile, loc["pages.per_file"]),
        new(PageGroupKind.EqualParts, loc["pages.equal_parts"]),
    ];
}

public sealed partial class AskPagesViewModel : ObservableObject
{
    private readonly ILocalizer _loc;

    /// <param name="path">The PDF.</param>
    /// <param name="pageCount">Its pages; null when they couldn't be counted (the range is then only checked for form).</param>
    /// <param name="preset">The preset dropped on: its range and its split start the window.</param>
    /// <param name="index">Which of the PDFs dropped together this is, from 0.</param>
    /// <param name="total">How many PDFs were dropped together.</param>
    public AskPagesViewModel(string path, int? pageCount, Preset preset, ILocalizer loc, int index = 0, int total = 1)
    {
        _loc = loc;
        FileName = Path.GetFileName(path);
        PageCount = pageCount;
        OffersSplit = AskedPages.OffersSplit(preset);
        _output = OffersSplit ? PageOutputs.Of(preset.Pdf) : PageOutput.OnePdf;
        GroupKinds = PageOutputs.Kinds(loc);
        _groupKind = GroupKinds.First(k => k.Value == PageOutputs.KindOf(preset.Pdf));
        _groupSize = PageOutputs.SizeOf(preset.Pdf);
        _range = preset.Pdf.PageRange;
        Position = total > 1 ? loc.Format("pages.file_of", index + 1, total) : "";
        PageCountText = pageCount is { } count ? loc.Format("pages.count", count) : loc["pages.count_unknown"];
        Update();
    }

    public string FileName { get; }
    public int? PageCount { get; }

    /// <summary>"File 2 of 3" when several PDFs were dropped together; empty for one.</summary>
    public string Position { get; }
    public bool ShowsPosition => Position.Length > 0;

    public string PageCountText { get; }

    /// <summary>One PDF, one file per page or several pages per file is offered (AskedPages.OffersSplit): a PDF target.</summary>
    public bool OffersSplit { get; }

    [ObservableProperty]
    private string _range;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnePdf), nameof(EachPage), nameof(Groups), nameof(SplitPages))]
    private PageOutput _output;

    /// <summary>The segments of the switch; a segment unchecked by the next one changes nothing.</summary>
    public bool OnePdf
    {
        get => Output == PageOutput.OnePdf;
        set
        {
            if (value)
                Output = PageOutput.OnePdf;
        }
    }

    public bool EachPage
    {
        get => Output == PageOutput.EachPage;
        set
        {
            if (value)
                Output = PageOutput.EachPage;
        }
    }

    /// <summary>Several pages per file: the number and its kind show below the switch.</summary>
    public bool Groups
    {
        get => Output == PageOutput.Groups;
        set
        {
            if (value)
                Output = PageOutput.Groups;
        }
    }

    /// <summary>The pages go into several files.</summary>
    public bool SplitPages => Output != PageOutput.OnePdf;

    public IReadOnlyList<Choice<PageGroupKind>> GroupKinds { get; }

    [ObservableProperty]
    private Choice<PageGroupKind> _groupKind;

    partial void OnGroupKindChanged(Choice<PageGroupKind>? oldValue, Choice<PageGroupKind> newValue) =>
        Choices.Restore(oldValue, newValue, v => GroupKind = v);

    /// <summary>With <see cref="Groups"/>: the pages per file, or the parts.</summary>
    [ObservableProperty]
    private int _groupSize;

    /// <summary>The range checked; the button starts nothing while it isn't valid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private PageRangeCheck _check;

    public bool IsValid => Check.IsValid;

    /// <summary>Below the box: how many pages the range takes, or what is wrong with it.</summary>
    [ObservableProperty]
    private string _status = "";

    partial void OnRangeChanged(string value) => Update();

    private void Update()
    {
        Check = PageRange.Check(Range, PageCount);
        Status = Check.Problem switch
        {
            PageRangeProblem.Invalid => _loc["pages.invalid"],
            PageRangeProblem.PastTheEnd => _loc.Format("pages.past_the_end", PageCount ?? 0),
            _ when Check.Pages is { } pages && PageCount is { } count => _loc.Format("pages.selected", pages, count),
            _ => "",
        };
    }

    /// <summary>The choice, or null while the range isn't valid.</summary>
    public PageChoice? Result()
    {
        if (!IsValid)
            return null;
        var (split, every, parts) = PageOutputs.Settings(OffersSplit ? Output : PageOutput.OnePdf, GroupKind.Value, GroupSize);
        return new PageChoice(Check.Text, split, every, parts);
    }
}
