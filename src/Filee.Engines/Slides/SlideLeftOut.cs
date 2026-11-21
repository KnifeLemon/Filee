// What the slide readers met that the slide renderer leaves out or draws another way, counted by kind for the deck's
// note (SlideDeck.LeftOut, "notice.renderer_left_out.<kind>"): PPTX and PPT through SlideCapabilities' pass over the
// package, ODP and ODG while OdfDrawReader reads. Each kind is named once, in a fixed order: what is left out first,
// then what is drawn another way; effects and WordArt have no count.

using Filee.Core.Conversion;

namespace Filee.Engines.Slides;

internal sealed class SlideLeftOut
{
    /// <summary>The kinds in the order the note names them.</summary>
    private static readonly string[] Order =
    [
        "charts", "smartart", "objects", "form_controls", "pictures", "shapes_left_out", "ink",
        "objects_pictured", "charts_pictured", "shapes", "wordart", "effects",
    ];

    /// <summary>Kinds named without a count: they are a look, not things one could count on the slide.</summary>
    private static readonly HashSet<string> Uncounted = ["wordart", "effects"];

    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public void Add(string kind, int count = 1) => _counts[kind] = _counts.GetValueOrDefault(kind) + count;

    public bool IsEmpty => _counts.Count == 0;

    /// <summary>The note's parts: each kind met once, with its count (0 for the uncounted ones).</summary>
    public List<LeftOutPart> Parts =>
        [.. _counts.OrderBy(pair => Array.IndexOf(Order, pair.Key) is var index and >= 0 ? index : Order.Length)
            .Select(pair => new LeftOutPart("notice.renderer_left_out." + pair.Key, Uncounted.Contains(pair.Key) ? 0 : pair.Value))];
}
