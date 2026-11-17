// Signed files (.p7m, #1010): a CMS / PKCS #7 SignedData envelope (RFC 5652; CAdES, as Italian e-invoices and signed
// PDFs are made) with the signed file inside. Filee takes that file out as it is, with .NET's SignedCms
// (System.Security.Cryptography.Pkcs): DER or BER, or the same as Base64 or PEM text, as some signing tools and mail
// programs save it. An envelope signed again (a second signature around the first) is opened again, up to MaxDepth.
//
// The signature is NOT checked, and nothing in it is trusted: no certificate chain is built, no revocation list or
// time-stamp server is asked, nothing goes over the network. Only the content is extracted, so the result is the plain
// file without the proof the signature gives (the .p7m stays as it was, for that).
//
// The file inside is named after the envelope (invoice.xml.p7m → invoice.xml) and its format comes from that name, else
// from its first bytes (PDF, pictures, XML, RTF, a MIME message, ZIP and the Office and OpenDocument files in one, plain
// text). JobQueue asks for that format (IEnvelopeOpener) and plans on from it, so P7M → PDF → JPG is one conversion.

using System.Buffers;
using System.Buffers.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Text.Unicode;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Web;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Signed;

/// <summary>Signed envelopes (.p7m) → the file inside them.</summary>
public sealed class P7mConverter : IConverter, IEnvelopeOpener
{
    /// <summary>
    /// The largest envelope opened: it is read whole, and the file inside is a second copy in memory. Signed scans and
    /// e-invoices with attachments are tens of megabytes.
    /// </summary>
    internal const long MaxSize = 512L * 1024 * 1024;

    /// <summary>Envelopes inside envelopes opened at most (each signature of a file signed again adds one).</summary>
    internal const int MaxDepth = 8;

    private const string SignedData = "1.2.840.113549.1.7.2";
    private const string EnvelopedData = "1.2.840.113549.1.7.3";

    public string Id => "p7m";
    public string DisplayName => "Signed files (P7M)";
    public int MaxParallelism => 0;

    /// <summary>The file inside can be of any format Filee reads: which one is known only from the file (<see cref="InnerFormat"/>).</summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
        [.. FormatRegistry.Known.Where(f => f.Id != "p7m").Select(f => new ConversionEdge("p7m", f.Id))];

    public EngineStatus GetStatus() => EngineStatus.Available("P7M → the signed file inside",
        $"{EngineVersions.BuiltIn} · {EngineVersions.Library("System.Security.Cryptography.Pkcs", typeof(SignedCms))}");

    public bool Opens(string format) => format == "p7m";

    public string InnerFormat(string path, string format) =>
        FormatOf(InnerName(path), Open(path)) ?? throw new ConversionRefusedException("error.p7m_unknown_content");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var content = Open(step.InputPath);
            var name = InnerName(step.OriginalSourcePath ?? step.InputPath);
            if (FormatOf(name, content) != step.To)
                throw new ConversionRefusedException("error.p7m_unknown_content");
            progress?.Report(0.5);
            cancellationToken.ThrowIfCancellationRequested();
            // The file's own spelling of its extension (".jpeg") when it has one of the format's.
            var extension = FormatRegistry.ExtensionOf(name);
            if (FormatRegistry.FindByExtension(extension)?.Id != step.To)
                extension = FormatRegistry.Get(step.To).PrimaryExtension;
            if (step.Output.Allocate(extension) is not { } output)
                return [];
            File.WriteAllBytes(output, content);
            progress?.Report(1);
            return [output];
        }, cancellationToken);

    /// <summary>The name of the file inside: the envelope's without ".p7m" (each of them, for one signed again).</summary>
    internal static string InnerName(string path)
    {
        var name = Path.GetFileName(path);
        while (name.Length > 4 && name.EndsWith(".p7m", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }

    /// <summary>
    /// The signed file inside the envelope at <paramref name="path"/>, out of every envelope around it. The signatures
    /// are not checked (see the top of the file).
    /// </summary>
    internal static byte[] Open(string path)
    {
        if (new FileInfo(path).Length > MaxSize)
            throw new ConversionRefusedException("error.p7m_too_large", $"{MaxSize / (1024 * 1024)} MB");
        var content = Unwrap(File.ReadAllBytes(path), outer: true)!;
        for (var depth = 1; depth < MaxDepth && Unwrap(content, outer: false) is { } inner; depth++)
            content = inner;
        return content;
    }

    /// <summary>
    /// The content of the SignedData in <paramref name="data"/> (binary, Base64 or PEM). For the
    /// <paramref name="outer"/> envelope anything else is refused with the reason; inside it, null means
    /// <paramref name="data"/> is the signed file itself.
    /// </summary>
    private static byte[]? Unwrap(byte[] data, bool outer)
    {
        var encoded = data is [0x30, ..] ? data : FromText(data);
        string? type = null;
        if (encoded is not null)
        {
            try
            {
                type = ContentInfo.GetContentType(encoded).Value;
            }
            catch (CryptographicException)
            {
            }
        }
        // S/MIME mail encrypted for someone (smime.p7m) is EnvelopedData: only the recipient's key opens it.
        if (type == EnvelopedData)
            throw new ConversionRefusedException("error.p7m_encrypted");
        if (type != SignedData || encoded is null)
            return outer ? throw new ConversionRefusedException("error.p7m_unreadable") : null;

        var cms = new SignedCms();
        try
        {
            cms.Decode(encoded);
        }
        catch (CryptographicException)
        {
            return outer ? throw new ConversionRefusedException("error.p7m_unreadable") : null;
        }
        // A detached signature (.p7s, or a .p7m made so) signs a file kept elsewhere and holds none.
        if (cms.ContentInfo.Content.Length == 0)
            return outer ? throw new ConversionRefusedException("error.p7m_detached") : null;
        return cms.ContentInfo.Content;
    }

    /// <summary>
    /// The DER bytes of an envelope saved as Base64 text, with or without PEM's "-----BEGIN …-----" lines, or null
    /// when <paramref name="data"/> is no such text or doesn't hold DER.
    /// </summary>
    private static byte[]? FromText(byte[] data)
    {
        ReadOnlySpan<byte> text = data;
        if (text.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            text = text[3..];
        var begin = text.IndexOf("-----BEGIN"u8);
        if (begin >= 0)
        {
            var line = text[begin..].IndexOf((byte)'\n');
            if (line < 0)
                return null;
            text = text[(begin + line + 1)..];
            var end = text.IndexOf("-----END"u8);
            if (end < 0)
                return null;
            text = text[..end];
        }
        var base64 = new byte[text.Length];
        var length = 0;
        foreach (var b in text)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                continue;
            if (!(b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
                    or (byte)'+' or (byte)'/' or (byte)'='))
                return null; // a binary file or other text: stops at its first byte that isn't Base64
            base64[length++] = b;
        }
        if (length == 0 || Base64.DecodeFromUtf8InPlace(base64.AsSpan(0, length), out var written) != OperationStatus.Done
            || written == 0 || base64[0] != 0x30)
            return null;
        return base64[..written];
    }

    /// <summary>
    /// The format id of the signed file: its name's when that is a format Filee knows, else the one its first bytes
    /// show; null when neither says.
    /// </summary>
    internal static string? FormatOf(string name, ReadOnlySpan<byte> content)
    {
        if (FormatRegistry.Detect(name) is { } named && named.Id != "p7m")
            return named.Id;
        return Sniff(content);
    }

    /// <summary>A format told by the first bytes alone, or null.</summary>
    internal static string? Sniff(ReadOnlySpan<byte> content)
    {
        var head = content[..Math.Min(content.Length, 4096)];
        if (WebFormats.Sniff(head) is { } extension)
            return FormatRegistry.FindByExtension(extension)?.Id;
        if (head.StartsWith("PK\x03\x04"u8))
            return ZipKind(content);
        var text = Encoding.UTF8.GetString(head).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (text.StartsWith("<?xml", StringComparison.Ordinal))
            return "xml";
        if (WebFormats.LooksLikePage(head))
            return "html";
        if (text.StartsWith(@"{\rtf", StringComparison.Ordinal))
            return "rtf";
        // A signed e-mail (S/MIME's smime.p7m) holds the MIME message.
        if (text.StartsWith("MIME-Version:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
            return "eml";
        return IsText(head, whole: head.Length == content.Length) ? "txt" : null;
    }

    /// <summary>A ZIP's format: Word, Excel and PowerPoint files and OpenDocument and EPUB by their parts, else ZIP.</summary>
    private static string ZipKind(ReadOnlySpan<byte> content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content.ToArray(), writable: false), ZipArchiveMode.Read);
            if (zip.GetEntry("mimetype") is { Length: < 256 } entry)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.ASCII);
                switch (reader.ReadToEnd().Trim())
                {
                    case "application/vnd.oasis.opendocument.text":
                        return "odt";
                    case "application/vnd.oasis.opendocument.spreadsheet":
                        return "ods";
                    case "application/vnd.oasis.opendocument.presentation":
                        return "odp";
                    case "application/epub+zip":
                        return "epub";
                }
            }
            if (zip.GetEntry("[Content_Types].xml") is not null)
            {
                if (zip.GetEntry("word/document.xml") is not null)
                    return "docx";
                if (zip.GetEntry("xl/workbook.xml") is not null)
                    return "xlsx";
                if (zip.GetEntry("ppt/presentation.xml") is not null)
                    return "pptx";
            }
        }
        catch (InvalidDataException)
        {
            // Not a ZIP Filee reads after all: the archive engine says what is wrong with it.
        }
        return "zip";
    }

    /// <summary>
    /// True for UTF-8 text without control characters other than tabs, line breaks and form feeds. Unless
    /// <paramref name="whole"/>, <paramref name="head"/> is the start of the file and may end inside a character.
    /// </summary>
    private static bool IsText(ReadOnlySpan<byte> head, bool whole)
    {
        if (head.IsEmpty)
            return false;
        foreach (var b in head)
        {
            if (b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C))
                return false;
        }
        var status = Utf8.ToUtf16(head, new char[head.Length], out _, out _, replaceInvalidSequences: false, isFinalBlock: whole);
        return status is OperationStatus.Done or OperationStatus.NeedMoreData;
    }
}
