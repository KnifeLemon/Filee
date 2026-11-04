// XML parts of ZIP packages (DOCX, PPTX, XLSX, HWPX, ODF, EPUB) and XML files of e-books, read with limits. A part a
// few kilobytes packed can unpack to gigabytes, so its size is checked while it is decompressed (a lying header does
// not help a zip bomb). Its nesting is checked by a quick pass of the reader before the tree is built: XDocument takes
// time that grows faster than the square of the depth (60,000 levels, 800 KB of XML, kept it busy for 14 seconds), and
// the tree's own operations (XElement.Value, copying) recurse once per level. DTDs are refused (DtdProcessing.Ignore
// where files are known to carry one) and nothing outside the package is ever fetched.

using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Filee.Core.Conversion;

namespace Filee.Engines.Infrastructure;

/// <summary>Reads XML parts with size and depth limits; one instance per package keeps the package's total.</summary>
internal sealed class BoundedXml
{
    /// <summary>
    /// Largest XML part read. Real parts stay well below: the document.xml of a 1,000-page report is some 30 MB, a
    /// worksheet of a million rows of a few numbers about 200 MB (and takes some 2 GB as a tree already).
    /// </summary>
    public const long MaxPartBytes = 256L * 1024 * 1024;

    /// <summary>Everything read from one package together: all parts of a long document with its headers, notes and sheets.</summary>
    public const long MaxPackageBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// Deepest element nesting read. Real documents nest a hundred levels or so (tables in tables in text boxes in
    /// content controls); what lies deeper than the readers follow (<see cref="Hwp.Hwpx.Nesting.MaxDepth"/>) is
    /// read as plain text, so this only refuses files no program writes.
    /// </summary>
    public const int MaxElementDepth = 1000;

    private readonly long _maxPart;
    private readonly long _maxPackage;
    private long _read;

    public BoundedXml() : this(MaxPartBytes, MaxPackageBytes)
    {
    }

    /// <summary>Smaller limits (for tests).</summary>
    internal BoundedXml(long maxPart, long maxPackage)
    {
        _maxPart = maxPart;
        _maxPackage = maxPackage;
    }

    /// <summary>A part of a package, counted against this package's total.</summary>
    /// <param name="options">As for <see cref="XDocument.Load(Stream, LoadOptions)"/>: whitespace is dropped unless preserved.</param>
    public XDocument Load(ZipArchiveEntry entry, LoadOptions options = LoadOptions.None)
    {
        var name = entry.FullName;
        var length = entry.Length; // not available any more once an entry of an archive opened for update is opened
        if (length > _maxPart)
            throw TooLarge(name, "over " + Size(_maxPart));
        using var stream = entry.Open();
        using var copy = new MemoryStream((int)Math.Min(length, 1024 * 1024));
        var buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (copy.Length + count > _maxPart)
                throw TooLarge(name, "over " + Size(_maxPart));
            _read += count;
            if (_read > _maxPackage)
                throw TooLarge(name, "the package unpacks to over " + Size(_maxPackage));
            copy.Write(buffer, 0, count);
        }
        return Parse(copy.GetBuffer(), (int)copy.Length, name, options, DtdProcessing.Prohibit);
    }

    /// <summary>One part on its own (with its own total).</summary>
    public static XDocument Part(ZipArchiveEntry entry, LoadOptions options = LoadOptions.None) => new BoundedXml().Load(entry, options);

    /// <summary>An XML file (an e-book's FB2 or package document).</summary>
    /// <param name="dtd">Ignore for formats whose files often start with a DOCTYPE; its entities are not expanded.</param>
    public static XDocument File(string path, LoadOptions options, DtdProcessing dtd)
    {
        var name = Path.GetFileName(path);
        if (new FileInfo(path).Length > MaxPartBytes)
            throw TooLarge(name, "over " + Size(MaxPartBytes));
        var bytes = System.IO.File.ReadAllBytes(path);
        return Parse(bytes, bytes.Length, name, options, dtd);
    }

    private static XDocument Parse(byte[] bytes, int length, string name, LoadOptions options, DtdProcessing dtd)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = dtd,
            XmlResolver = null,
            IgnoreWhitespace = !options.HasFlag(LoadOptions.PreserveWhitespace),
        };
        // The depth pass reads the elements only; an XmlException it throws is the one XDocument.Load would throw.
        using (var reader = XmlReader.Create(new MemoryStream(bytes, 0, length, writable: false), settings))
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth >= MaxElementDepth)
                    throw TooLarge(name, $"elements nested over {MaxElementDepth} levels");
            }
        }
        using (var reader = XmlReader.Create(new MemoryStream(bytes, 0, length, writable: false), settings))
            return XDocument.Load(reader, options);
    }

    /// <summary>A size for a message: "256 MB", or bytes below 1 MB.</summary>
    internal static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes} bytes";

    private static ConversionRefusedException TooLarge(string part, string reason) =>
        new("error.document_too_large", $"{part}: {reason}");
}
