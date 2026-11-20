// Shared by the writers of the older e-book formats (PDB, TCR, PML, RB, SNB): the text encoding their readers
// expect, the size limit a written book stays within (the same one the readers apply, so a book Filee writes is a
// book Filee reads), and pictures as PNG for the formats whose devices show nothing else.

using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static class LegacyWriting
{
    /// <summary>Windows-1252 that throws on characters it lacks (instead of writing '?').</summary>
    public static Encoding StrictWindows1252
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
    }

    /// <summary>Windows-1252 that writes '?' for characters it lacks (names in fixed-size fields).</summary>
    public static Encoding Windows1252
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
    }

    /// <summary>
    /// Text as Windows-1252 when every character has a byte there (what Palm and Psion readers were made for), else
    /// as UTF-8 without a byte order mark (Filee's readers and calibre take valid UTF-8 first).
    /// </summary>
    public static (byte[] Bytes, Encoding Encoding) Encode(string text)
    {
        var windows1252 = StrictWindows1252;
        try
        {
            return (windows1252.GetBytes(text), windows1252);
        }
        catch (EncoderFallbackException)
        {
            var utf8 = new UTF8Encoding(false);
            return (utf8.GetBytes(text), utf8);
        }
    }

    /// <summary>A document as plain text (<see cref="PlainTextWriter"/>) with <paramref name="newLine"/> line ends.</summary>
    public static string PlainText(HDocument document, string newLine) =>
        PlainTextWriter.Write(document).Replace("\r\n", "\n").Replace("\n", newLine);

    /// <summary>Refuses a book whose text passes the limit the readers apply (64 MB): no reader would open it.</summary>
    public static void CheckText(long bytes, string format)
    {
        var limit = DocumentLimits.Default.MaxPartBytes;
        if (bytes > limit)
            throw DocumentLimits.TooLarge($"The {format} book's text would be larger than {DocumentLimits.Megabytes(limit)}.");
    }

    /// <summary>
    /// The picture at <paramref name="path"/> as a PNG file (converted into <paramref name="folder"/> when it is
    /// another format), or null when it cannot be read.
    /// </summary>
    public static string? Png(string path, string folder)
    {
        if (EbookFiles.ImageMediaType(path) == "image/png")
            return File.Exists(path) ? path : null;
        try
        {
            using var image = new ImageMagick.MagickImage(path);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"{Guid.NewGuid():N}.png");
            image.Write(file, ImageMagick.MagickFormat.Png);
            return file;
        }
        catch (Exception ex) when (ex is ImageMagick.MagickException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
