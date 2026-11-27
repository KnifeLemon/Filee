// What FFmpeg is given to read a file, for every action that hands it one (convert, caption, Live / Motion Photo,
// merge, the Cut a clip editor, size estimates). Most files are given as they are. A Wisenet WAVE / Nx Witness export
// (.nov) is given as its camera recording, read where it lies through the subfile protocol (NovExport); a Hikvision
// SD-card recording with mu-law sound as a concat of the IMKH header FFmpeg needs and the file (HikvisionStream).
// A DVD-Video disc image (.iso) is given as its longest title (DvdVideoIso), read by FFmpeg's dvdvideo demuxer: that
// needs "-f dvdvideo -title N" before "-i", so the input is a "dvdvideo:N:path" marker that Arguments turns into those
// options where FFmpeg and ffprobe are run.

using System.Globalization;
using Filee.Core.Formats;
using Filee.Core.Platform;

namespace Filee.Engines.Media;

internal static class MediaInput
{
    /// <summary>
    /// Protocols the concat demuxer must allow for a list of these inputs (Merge videos): it allows only file, crypto
    /// and data by default, whatever the command line allows.
    /// </summary>
    public const string ListProtocols = "file,crypto,data,subfile,concat";

    /// <summary>
    /// The input to give FFmpeg for <paramref name="path"/>, a file of <paramref name="format"/> (detected from its
    /// name when null). <paramref name="workDirectory"/> holds what Filee writes for it (the Hikvision header); without
    /// one, a folder in Filee's work folder does. <paramref name="notice"/> hears what the user should know, as
    /// a localization key (a .nov export's other recordings left out). Refuses a protected .nov export or one without a
    /// recording (<see cref="Filee.Core.Conversion.ConversionRefusedException"/>), and throws
    /// <see cref="InvalidDataException"/> for a damaged one.
    /// </summary>
    public static string For(string path, string? format = null, string? workDirectory = null, Action<string>? notice = null)
    {
        var full = Path.GetFullPath(path);
        format ??= FormatRegistry.Detect(full)?.Id;
        if (format == "nov")
            return NovExport.InputFor(full, notice);
        if (format == DvdVideoIso.Format)
            return DvdVideoIso.InputFor(full, notice);
        // Only a video can be a Hikvision recording: sound files and pictures aren't looked into.
        if (format is not null && FormatRegistry.FindById(format) is { Category: not FormatCategory.Video })
            return full;
        return HikvisionStream.InputFor(full, workDirectory ?? SharedFolder);
    }

    /// <summary>True for an input <see cref="For"/> made a protocol URL or a DVD title of, rather than a path.</summary>
    public static bool IsUrl(string input) =>
        input.StartsWith("subfile,", StringComparison.Ordinal) || input.StartsWith("concat:", StringComparison.Ordinal)
        || IsDvdVideo(input);

    private const string DvdVideoMarker = DvdVideoIso.Demuxer + ":";

    /// <summary>The input for title <paramref name="title"/> of the DVD-Video image at <paramref name="path"/>.</summary>
    public static string DvdVideo(string path, int title) =>
        string.Create(CultureInfo.InvariantCulture, $"{DvdVideoMarker}{title}:{path}");

    /// <summary>True for a DVD title made by <see cref="DvdVideo"/>.</summary>
    public static bool IsDvdVideo(string input) => input.StartsWith(DvdVideoMarker, StringComparison.Ordinal);

    /// <summary>
    /// The options and input that open <paramref name="input"/>: <c>-f dvdvideo -title N -i path</c> for a DVD title,
    /// else <c>-i input</c>.
    /// </summary>
    public static IEnumerable<string> Open(string input)
    {
        if (!IsDvdVideo(input))
            return ["-i", input];
        var rest = input[DvdVideoMarker.Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        return ["-f", DvdVideoIso.Demuxer, "-title", rest[..colon], "-i", rest[(colon + 1)..]];
    }

    /// <summary>
    /// <paramref name="arguments"/> of an FFmpeg or ffprobe run with every DVD title opened by <see cref="Open"/>,
    /// whether it follows <c>-i</c> or stands alone (ffprobe's input); other arguments are returned as they are.
    /// </summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> arguments)
    {
        if (!arguments.Any(IsDvdVideo))
            return arguments;
        var expanded = new List<string>(arguments.Count + 6);
        foreach (var argument in arguments)
        {
            if (!IsDvdVideo(argument))
            {
                expanded.Add(argument);
                continue;
            }
            if (expanded.Count > 0 && expanded[^1] == "-i")
                expanded.RemoveAt(expanded.Count - 1);
            expanded.AddRange(Open(argument));
        }
        return expanded;
    }

    /// <summary>The file a DVD title made by <see cref="DvdVideo"/> reads, or <paramref name="input"/> itself.</summary>
    public static string DvdVideoPath(string input) => IsDvdVideo(input) ? Open(input).Last() : input;

    /// <summary>
    /// Where the Hikvision header is written for actions without a work folder (the Cut a clip editor, estimates): in
    /// Filee's own work folder, private to the user (mode 700 on Linux and macOS), so nobody else can put a file in its
    /// place. It is the same 40 bytes every time, so it is left there.
    /// </summary>
    private static string SharedFolder => Path.Combine(WorkFolders.Root, "media");
}
