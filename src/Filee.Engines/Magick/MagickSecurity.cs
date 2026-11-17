// ImageMagick reads a few hundred formats and picks one by the file's content, whatever the file is called: a picture
// in a document or a book named cover.jpg can be PostScript (run by Ghostscript when it is installed), an HTML page, a
// text file or a URL. Filee hands ImageMagick the raster formats it converts, so before ImageMagick's first use its
// policy is set to register only the modules of those formats (ImageMagick reads its policy once, when it starts).
// SVG stays: HTML pictures are rasterized through it, by librsvg, which loads nothing from outside the SVG's folder;
// MSVG, ImageMagick's own SVG renderer that follows any file or URL, stays off. The policy also limits the pixels
// ImageMagick holds: a header of a few bytes can claim a picture of millions of pixels a side.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Filee.Core.Platform;
using ImageMagick;
using ImageMagick.Configuration;

namespace Filee.Engines.Magick;

/// <summary>Sets ImageMagick's security policy and resource limits before it is first used.</summary>
internal static class MagickSecurity
{
    /// <summary>
    /// The ImageMagick modules Filee reads or writes (a module holds a format and its variants: JPEG also JPG and JPE,
    /// PNM the Netpbm family, DNG every camera RAW format, HEIC also AVIF). XC, GRADIENT and PLASMA make pictures
    /// out of nothing, and RGB, GRAY and CMYK are raw pixel data. PDF is only written: reading it would run Ghostscript.
    /// DIB, a BMP without its file header, is looked up by Magick.NET whenever it lists its formats.
    /// </summary>
    internal static readonly string[] Modules =
    [
        "BMP", "CMYK", "DCM", "DDS", "DIB", "DNG", "EMF", "EXR", "GIF", "GRADIENT", "GRAY", "HDR", "HEIC", "ICON", "JP2",
        "JPEG", "JXL", "MIFF", "PCD", "PCX", "PDF", "PLASMA", "PNG", "PNM", "PSD", "QOI", "RGB", "SGI", "SUN", "SVG", "TGA",
        "TIFF", "WBMP", "WEBP", "XBM", "XC", "XCF", "XPM",
    ];

    /// <summary>
    /// Widest and tallest picture: far beyond a "Long image" of a PDF (PdfiumConverter.MaxStackedPixels at a narrow
    /// page) or a panorama.
    /// </summary>
    internal const int MaxSide = 1_048_576;

    /// <summary>
    /// The policy, after the defaults Magick.NET comes with. The last policy that matches decides, so every module
    /// is refused first and those of <see cref="Modules"/> allowed again. Pictures larger than the memory and area
    /// limits are kept on disk, up to the disk limit; more frames than the list length are refused.
    /// </summary>
    internal static string Policy(string defaults) => defaults.Replace("</policymap>", $$"""
          <policy domain="resource" name="memory" value="8GiB"/>
          <policy domain="resource" name="map" value="8GiB"/>
          <policy domain="resource" name="area" value="2GP"/>
          <policy domain="resource" name="disk" value="16GiB"/>
          <policy domain="resource" name="width" value="{{MaxSide}}"/>
          <policy domain="resource" name="height" value="{{MaxSide}}"/>
          <policy domain="resource" name="list-length" value="32768"/>
          <policy domain="module" rights="none" pattern="*"/>
          <policy domain="module" rights="read | write" pattern="{{{string.Join(',', Modules)}}}"/>
          <policy domain="coder" rights="write" pattern="{PDF,PDFA,EPDF,AI,POCKETMOD}"/>
          <policy domain="coder" rights="none" pattern="MSVG"/>
        </policymap>
        """, StringComparison.Ordinal);

    private static readonly Lock Gate = new();
    private static bool _initialized;

    /// <summary>Runs when Filee.Engines is loaded, before any of its code can use ImageMagick.</summary>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "ImageMagick reads its policy once, at its first use; it must be in place before.")]
    internal static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
                return;
            _initialized = true;
            var files = ConfigurationFiles.Default;
            files.Policy.Data = Policy(files.Policy.Data);
            try
            {
                MagickNET.Initialize(Folder(files));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The shared folder could not be written or checked: a folder of this process's own.
                try
                {
                    MagickNET.Initialize(files);
                }
                catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
                {
                    // Nowhere to write: ImageMagick keeps Magick.NET's defaults (MVG, MSL and "@file" are off there).
                }
            }
        }
    }

    /// <summary>
    /// A folder with the configuration files in Filee's work root, named after their content, so every Filee process
    /// with the same files shares it instead of leaving one behind each. A folder whose files were changed is not used.
    /// The work root is the user's own (%TEMP%\Filee, or the cache folder with mode 700 on Linux and macOS, see
    /// <see cref="WorkFolders"/>): in a shared /tmp another user could make the folder first and loosen the policy.
    /// </summary>
    private static string Folder(IConfigurationFiles files)
    {
        var all = files.All.ToList();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(all.Select(f => f.FileName + "\0" + f.Data + "\0"))));
        var folder = Path.Combine(WorkFolders.Root, "magick-" + Convert.ToHexStringLower(hash)[..16]);
        if (!Directory.Exists(folder))
        {
            var staging = folder + "-" + Guid.NewGuid().ToString("N")[..8];
            Directory.CreateDirectory(staging);
            foreach (var file in all)
                File.WriteAllText(Path.Combine(staging, file.FileName), file.Data);
            try
            {
                Directory.Move(staging, folder);
            }
            catch (IOException) when (Directory.Exists(folder))
            {
                Directory.Delete(staging, recursive: true); // another Filee process was first
            }
        }
        foreach (var file in all)
        {
            if (File.ReadAllText(Path.Combine(folder, file.FileName)) != file.Data)
                throw new IOException($"{folder} holds other configuration files.");
        }
        return folder;
    }
}
