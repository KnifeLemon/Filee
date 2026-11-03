// "Annotate" runs as an ordinary conversion once its window has the marks: the tool preset becomes a conversion into
// the picture's own format with the marks (ImageOptions.Annotations), so the job queue, the toast, cancelling and the
// output rules work as for any other file. The colour and width picked last are kept in the tool.

using Filee.Core.Conversion;

namespace Filee.Core.Presets;

public static class AnnotateTool
{
    /// <summary>The colours the window offers, in order (the first is the one it starts with).</summary>
    public static readonly string[] Colors = ["#E5484D", "#F5A524", "#F9D84A", "#30A46C", "#3E63DD", "#1C1C1C"];

    /// <summary>The names of <see cref="Colors"/>, in order: what a screen reader says for each.</summary>
    public static readonly string[] ColorNameKeys = ["annotate.red", "annotate.orange", "annotate.yellow", "annotate.green", "annotate.blue", "annotate.black"];

    /// <summary>Marks too small to mean anything (a click, not a drag), in parts of the picture's side.</summary>
    public const double MinSize = 0.004;

    /// <summary>
    /// The format a picture is written in: its own when it can be written, else PNG (as Pixelate / blur).
    /// </summary>
    public static string TargetFor(string sourceFormat, RoutePlanner planner) => HideTool.TargetFor(sourceFormat, planner);

    /// <summary>Writes a picture as <paramref name="targetFormat"/> with <paramref name="marks"/> on it.</summary>
    public static Preset ConversionOf(Preset tool, string targetFormat, IEnumerable<Annotation> marks)
    {
        var preset = tool.Clone();
        preset.Kind = PresetKind.Convert;
        preset.Tool = null;
        preset.SubPresetIds = [];
        preset.TargetFormat = targetFormat;
        preset.Image.Annotations = [.. marks.Where(IsDrawable).Select(mark => mark.Clone())];
        preset.Output.OnlyIfSmaller = false;
        return preset;
    }

    /// <summary>A mark with what it needs to be drawn: points for its kind, and text for a text.</summary>
    public static bool IsDrawable(Annotation mark) => mark.Kind switch
    {
        AnnotationKind.Pen => mark.Points.Count >= 2,
        AnnotationKind.Arrow or AnnotationKind.Box or AnnotationKind.Highlight => mark.Points.Count >= 2,
        AnnotationKind.Text => mark.Points.Count >= 1 && !string.IsNullOrWhiteSpace(mark.Text),
        AnnotationKind.Number => mark.Points.Count >= 1,
        _ => false,
    };

    /// <summary>Keeps the colour and width picked last in <paramref name="tool"/>.</summary>
    public static void Remember(Preset tool, string color, AnnotationWidth width)
    {
        tool.Image.AnnotationColor = color;
        tool.Image.AnnotationWidth = width;
    }
}
