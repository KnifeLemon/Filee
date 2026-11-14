// The slide decks Filee's slide renderer reads (PptxSlideReader): the Filee renderer takes them to PDF and to a
// picture per slide. A reader for another deck format adds its format here.

namespace Filee.Engines.Slides;

internal static class SlideFormats
{
    /// <summary>
    /// PowerPoint 2007+ decks: presentations, macro-enabled ones, templates and slide shows; and PowerPoint 97–2003
    /// (.ppt, .pps, .pot), read as the PPTX package it translates to (SlidePackage).
    /// </summary>
    public static readonly string[] Decks = ["pptx", "pptm", "potx", "potm", "ppsx", "ppt"];

    /// <summary>What a deck becomes through the slide renderer.</summary>
    public static readonly string[] Targets = ["pdf", "png", "jpg"];
}
