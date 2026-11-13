// What FFmpeg is given to read a file, for every action that hands it one (convert, caption, Live / Motion Photo,
// merge, the Cut a clip editor, size estimates). Most files are given as they are. A Wisenet WAVE / Nx Witness export
// (.nov) is given as its camera recording, read where it lies through the subfile protocol (NovExport); a Hikvision
// SD-card recording with mu-law sound as a concat of the IMKH header FFmpeg needs and the file (HikvisionStream).

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
        // Only a video can be a Hikvision recording: sound files and pictures aren't looked into.
        if (format is not null && FormatRegistry.FindById(format) is { Category: not FormatCategory.Video })
            return full;
        return HikvisionStream.InputFor(full, workDirectory ?? SharedFolder);
    }

    /// <summary>True for an input <see cref="For"/> made a protocol URL of, rather than a path.</summary>
    public static bool IsUrl(string input) =>
        input.StartsWith("subfile,", StringComparison.Ordinal) || input.StartsWith("concat:", StringComparison.Ordinal);

    /// <summary>
    /// Where the Hikvision header is written for actions without a work folder (the Cut a clip editor, estimates): in
    /// Filee's own work folder, private to the user (mode 700 on Linux and macOS), so nobody else can put a file in its
    /// place. It is the same 40 bytes every time, so it is left there.
    /// </summary>
    private static string SharedFolder => Path.Combine(WorkFolders.Root, "media");
}
