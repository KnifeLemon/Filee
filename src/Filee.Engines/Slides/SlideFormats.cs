// The slide decks Filee's slide renderer reads (PptxSlideReader): the Filee renderer takes them to PDF and to a
// picture per slide. A reader for another deck format adds its format here.

namespace Filee.Engines.Slides;

internal static class SlideFormats
{
    /// <summary>PowerPoint 2007+ decks: presentations, macro-enabled ones, templates and slide shows.</summary>
    public static readonly string[] Decks = ["pptx", "pptm", "potx", "potm", "ppsx"];

    /// <summary>What a deck becomes through the slide renderer.</summary>
    public static readonly string[] Targets = ["pdf", "png", "jpg"];
}
