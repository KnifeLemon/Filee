// .m4s files (#354): a fragmented MP4 with its own moov (ftyp + moov, then styp / sidx / moof / mdat fragments) is read
// by FFmpeg like any MP4. A DASH media segment holds only fragments and plays with a separate init segment (the moov);
// on its own FFmpeg fails with a demuxer error, so Filee refuses it first with a message that says why.

using Filee.Core.Conversion;

namespace Filee.Engines.Media;

internal static class DashSegment
{
    /// <summary>Top-level boxes looked at before giving up; a moov or moof comes well before this in real files.</summary>
    private const int MaxBoxes = 4096;

    /// <summary>
    /// Refuses an <c>.m4s</c> that is a media segment without its init segment; any other file (or one that isn't
    /// ISO BMFF at all) is left to FFmpeg.
    /// </summary>
    public static void ThrowIfNeedsInitSegment(string path)
    {
        if (!Path.GetExtension(path).Equals(".m4s", StringComparison.OrdinalIgnoreCase))
            return;
        bool needsInit;
        try
        {
            using var file = File.OpenRead(path);
            needsInit = NeedsInitSegment(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // FFmpeg reports what is wrong with the file.
        }
        if (needsInit)
            throw new ConversionRefusedException("error.m4s_needs_init");
    }

    /// <summary>
    /// True when the top-level boxes reach a <c>moof</c> (a movie fragment) before any <c>moov</c>: the fragments
    /// can't be decoded without the moov of a separate init segment. False when a moov comes first, and for data that
    /// isn't a box structure.
    /// </summary>
    public static bool NeedsInitSegment(Stream file)
    {
        try
        {
            for (var i = 0; i < MaxBoxes && file.Length - file.Position >= 8; i++)
            {
                var box = MovieBoxes.ReadHeader(file);
                switch (box.Type)
                {
                    case "moov":
                        return false;
                    case "moof":
                        return true;
                }
                file.Position = box.End;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException)
        {
            // Not ISO BMFF, or damaged: FFmpeg decides.
        }
        return false;
    }
}
