// "Add background" runs as an ordinary conversion once its window has the look: the tool preset becomes a conversion
// into PNG with the look (ImageOptions.Backdrop), so the job queue, the toast, cancelling and the output rules work as
// for any other file. The look is kept in the tool, so the window opens with the last one.

namespace Filee.Core.Presets;

public static class BackgroundTool
{
    /// <summary>
    /// Always PNG: the rounded corners and the shadow need no format of their own, and nothing is lost.
    /// </summary>
    public const string Target = "png";

    /// <summary>The look the tool starts with before it was ever used.</summary>
    public static BackdropOptions DefaultLook => new();

    /// <summary>The look kept in <paramref name="tool"/>, within its ranges.</summary>
    public static BackdropOptions LookOf(Preset tool) => (tool.Image.BackdropLook ?? DefaultLook).Clamped();

    /// <summary>
    /// The conversion that writes a picture set on <paramref name="look"/>, with the tool's name and output rule. The
    /// tool itself is left as it is.
    /// </summary>
    public static Preset ConversionOf(Preset tool, BackdropOptions look)
    {
        var preset = tool.Clone();
        preset.Kind = PresetKind.Convert;
        preset.Tool = null;
        preset.SubPresetIds = [];
        preset.TargetFormat = Target;
        preset.Image.Backdrop = look.Clamped();
        preset.Image.BackdropLook = null;
        // "Only if smaller" would keep the original whenever the picture on its background comes out larger.
        preset.Output.OnlyIfSmaller = false;
        return preset;
    }

    /// <summary>Keeps <paramref name="look"/> in <paramref name="tool"/> for the next time.</summary>
    public static void Remember(Preset tool, BackdropOptions look)
    {
        // Not the stamp of the picture behind: it is about its file as it was then.
        var kept = look.Clamped();
        kept.PictureWritten = null;
        kept.PictureLength = null;
        tool.Image.BackdropLook = kept;
    }
}
