// An OpenDocument package (ODF 1.3 Part 2) as the writers of draw:page documents make it: "mimetype" first, stored
// uncompressed and without an extra field so the type can be read at a fixed offset; the XML parts and pictures; and
// META-INF/manifest.xml listing every part with its media type, the package itself under "/". The file is written
// through a temporary file of its own and replaces the output at the end, so a cancelled or failed write leaves nothing
// half done. Text goes out as UTF-8 without a byte order mark.

using System.IO.Compression;
using System.Text;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Odf;

/// <summary>One part of a package: XML text or the bytes of a picture.</summary>
internal sealed record OdfPart(string Path, string MediaType, string? Text = null, byte[]? Bytes = null);

internal static class OdfPackageWriter
{
    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

    /// <summary>Writes the package at <paramref name="outputPath"/> (replaced atomically).</summary>
    public static void Write(string outputPath, string mimeType, string version, IReadOnlyList<OdfPart> parts, CancellationToken cancellationToken)
    {
        var manifest = new StringBuilder(Declaration)
            .Append($"<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"{version}\">")
            .Append($"<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"{version}\" manifest:media-type=\"{mimeType}\"/>");
        foreach (var part in parts)
            manifest.Append($"<manifest:file-entry manifest:full-path=\"{OdfXml.Escape(part.Path)}\" manifest:media-type=\"{OdfXml.Escape(part.MediaType)}\"/>");
        manifest.Append("</manifest:manifest>");

        WriterTempFile.Write(outputPath, file =>
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            Text(zip, "mimetype", mimeType, CompressionLevel.NoCompression);
            foreach (var part in parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (part.Text is { } text)
                {
                    Text(zip, part.Path, text, CompressionLevel.Optimal);
                }
                else if (part.Bytes is { } bytes)
                {
                    // Compressed pictures (PNG, JPEG, GIF) gain nothing from deflating again.
                    using var target = zip.CreateEntry(part.Path, Compressed(bytes) ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open();
                    target.Write(bytes);
                }
            }
            Text(zip, "META-INF/manifest.xml", manifest.ToString(), CompressionLevel.Optimal);
        });
    }

    /// <summary>An XML part's text: the declaration and the root element.</summary>
    public static string Xml(string root) => Declaration + root;

    private static bool Compressed(byte[] bytes) =>
        bytes.Length >= 4 && ((bytes[0] == 0x89 && bytes[1] == (byte)'P') || (bytes[0] == 0xFF && bytes[1] == 0xD8) || (bytes[0] == (byte)'G' && bytes[1] == (byte)'I'));

    private static void Text(ZipArchive zip, string name, string text, CompressionLevel level)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}

/// <summary>XML text and attribute values as the ODF writers put them out.</summary>
internal static class OdfXml
{
    /// <summary>
    /// Escapes markup characters and leaves out what XML 1.0 can't hold: control characters other than tab, line feed
    /// and carriage return, U+FFFE / U+FFFF and surrogates without their pair.
    /// </summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(ch).Append(text[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(ch) || (ch < 0x20 && ch is not ('\t' or '\n' or '\r')) || ch is '￾' or '￿')
                continue;
            switch (ch)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '"':
                    sb.Append("&quot;");
                    break;
                case '\'':
                    sb.Append("&apos;");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
