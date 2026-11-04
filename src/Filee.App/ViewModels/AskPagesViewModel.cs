// "Choose pages" (#981): the pages of one PDF dropped on a preset that asks for them (PdfOptions.AskPageRange), and for
// a PDF target whether they become one PDF or one file per page. The range is checked as it is typed.

using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

/// <summary>What the user chose: the range as it is used (empty for every page) and one file per page.</summary>
public sealed record PageChoice(string Range, bool SplitPages);

public sealed partial class AskPagesViewModel : ObservableObject
{
    private readonly ILocalizer _loc;

    /// <param name="path">The PDF.</param>
    /// <param name="pageCount">Its pages; null when they couldn't be counted (the range is then only checked for form).</param>
    /// <param name="preset">The preset dropped on: its range and its one-file-per-page start the window.</param>
    /// <param name="index">Which of the PDFs dropped together this is, from 0.</param>
    /// <param name="total">How many PDFs were dropped together.</param>
    public AskPagesViewModel(string path, int? pageCount, Preset preset, ILocalizer loc, int index = 0, int total = 1)
    {
        _loc = loc;
        FileName = Path.GetFileName(path);
        PageCount = pageCount;
        OffersSplit = AskedPages.OffersSplit(preset);
        _splitPages = OffersSplit && preset.Pdf.SplitPages;
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

    /// <summary>One PDF or one file per page is offered (AskedPages.OffersSplit): a PDF target.</summary>
    public bool OffersSplit { get; }

    [ObservableProperty]
    private string _range;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnePdf))]
    private bool _splitPages;

    /// <summary>The other choice of the switch: the pages stay together in one PDF.</summary>
    public bool OnePdf
    {
        get => !SplitPages;
        set => SplitPages = !value;
    }

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
    public PageChoice? Result() => IsValid ? new PageChoice(Check.Text, OffersSplit && SplitPages) : null;
}
