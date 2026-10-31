// Reads HWP 5.0 (binary 한글) documents without rhwp or Hancom Office: the compound file's records are written out as
// the HWPX package 한글 itself would save (HwpToHwpx) and read by HwpxReader, so both 한글 formats share one reader and
// every mapping of it, the stored line layout included. The same package is Filee's HWP → HWPX conversion.
//
// An .hwp that is really an HWPX package (renamed, or saved by a program that names its HWPX files .hwp) is read as
// HWPX. Password, DRM and distribution documents and HWP 3.x files are refused by HwpFile with a message.

using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Hwp.Binary;

/// <summary>Reads .hwp files into the HWPX document model.</summary>
internal static class HwpReader
{
    /// <summary>Reads <paramref name="path"/>; embedded pictures are extracted into <paramref name="mediaFolder"/>.</summary>
    public static HDocument Read(string path, string mediaFolder) => Read(path, mediaFolder, null, forLayout: false, DocumentLimits.Default);

    /// <param name="skipped">Receives what the model can't hold (see <see cref="HwpxReader.Read(string, string, IDictionary{string, int}?)"/>).</param>
    public static HDocument Read(string path, string mediaFolder, IDictionary<string, int>? skipped, DocumentLimits? limits = null) =>
        Read(path, mediaFolder, skipped, forLayout: false, limits ?? DocumentLimits.Default);

    /// <summary>Reads <paramref name="path"/> for the layout engine (see <see cref="HwpxReader.ReadForLayout"/>).</summary>
    public static HDocument ReadForLayout(string path, string mediaFolder, IDictionary<string, int>? skipped = null, DocumentLimits? limits = null) =>
        Read(path, mediaFolder, skipped, forLayout: true, limits ?? DocumentLimits.Default);

    /// <summary>Writes the HWPX package of the HWP document at <paramref name="path"/> to <paramref name="output"/>.</summary>
    public static void ToHwpx(string path, string output, DocumentLimits? limits = null)
    {
        if (HwpFile.IsZip(path))
        {
            File.Copy(path, output, overwrite: true); // already HWPX
            return;
        }
        using var file = HwpFile.Open(path, limits ?? DocumentLimits.Default);
        HwpToHwpx.Write(file, output);
    }

    private static HDocument Read(string path, string mediaFolder, IDictionary<string, int>? skipped, bool forLayout, DocumentLimits limits)
    {
        if (HwpFile.IsZip(path))
            return forLayout ? HwpxReader.ReadForLayout(path, mediaFolder, skipped, limits) : HwpxReader.Read(path, mediaFolder, skipped, limits);

        // The package goes next to the media folder, in the job's scratch folder.
        var folder = Path.GetDirectoryName(Path.GetFullPath(mediaFolder)) ?? Path.GetTempPath();
        Directory.CreateDirectory(folder);
        var hwpx = Path.Combine(folder, $"hwp-{Guid.NewGuid():N}.hwpx");
        try
        {
            ToHwpx(path, hwpx, limits);
            return forLayout ? HwpxReader.ReadForLayout(hwpx, mediaFolder, skipped, limits) : HwpxReader.Read(hwpx, mediaFolder, skipped, limits);
        }
        finally
        {
            File.Delete(hwpx);
        }
    }
}
