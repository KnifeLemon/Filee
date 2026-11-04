// XML parts of ZIP packages (DOCX, PPTX, XLSX, HWPX, ODF, EPUB) and XML files of e-books, read with limits. A part a
// few kilobytes packed can unpack to gigabytes, so its size is checked while it is decompressed (a lying header does
// not help a zip bomb). Its nesting is checked by the reader the tree is built from, which stops at MaxElementDepth:
// XDocument takes time that grows faster than the square of the depth (60,000 levels, 800 KB of XML, kept it busy for
// 14 seconds), and the tree's own operations (XElement.Value, copying) recurse once per level. Both are checked in one
// pass, straight from the decompressing stream. DTDs are refused (DtdProcessing.Ignore where files are known to carry
// one) and nothing outside the package is ever fetched.

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
        using var stream = new CountingStream(entry.Open(), count =>
        {
            if (count > _maxPart)
                throw TooLarge(name, "over " + Size(_maxPart));
            if (_read + count > _maxPackage)
                throw TooLarge(name, "the package unpacks to over " + Size(_maxPackage));
        });
        try
        {
            return Parse(stream, name, options, DtdProcessing.Prohibit);
        }
        finally
        {
            _read += stream.Count;
        }
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
        using var stream = System.IO.File.OpenRead(path);
        return Parse(stream, name, options, dtd);
    }

    private static XDocument Parse(Stream stream, string name, LoadOptions options, DtdProcessing dtd)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = dtd,
            XmlResolver = null,
            IgnoreWhitespace = !options.HasFlag(LoadOptions.PreserveWhitespace),
        };
        using var reader = new DepthLimitedReader(XmlReader.Create(stream, settings), name);
        return XDocument.Load(reader, options);
    }

    /// <summary>A size for a message: "256 MB", or bytes below 1 MB.</summary>
    internal static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes} bytes";

    private static ConversionRefusedException TooLarge(string part, string reason) =>
        new("error.document_too_large", $"{part}: {reason}");

    /// <summary>A read-only stream that shows the number of bytes read so far to <c>check</c> before handing them on.</summary>
    private sealed class CountingStream(Stream inner, Action<long> check) : Stream
    {
        public long Count { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

        private int Counted(int read)
        {
            check(Count + read);
            Count += read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Hands on the nodes of another reader and stops at an element nested <see cref="MaxElementDepth"/> levels deep.</summary>
    private sealed class DepthLimitedReader(XmlReader inner, string name) : XmlReader
    {
        public override bool Read()
        {
            if (!inner.Read())
                return false;
            if (inner.NodeType == XmlNodeType.Element && inner.Depth >= MaxElementDepth)
                throw TooLarge(name, $"elements nested over {MaxElementDepth} levels");
            return true;
        }

        public override XmlNodeType NodeType => inner.NodeType;
        public override string LocalName => inner.LocalName;
        public override string NamespaceURI => inner.NamespaceURI;
        public override string Prefix => inner.Prefix;
        public override string Name => inner.Name;
        public override string Value => inner.Value;
        public override bool HasValue => inner.HasValue;
        public override int Depth => inner.Depth;
        public override string BaseURI => inner.BaseURI;
        public override bool IsEmptyElement => inner.IsEmptyElement;
        public override bool IsDefault => inner.IsDefault;
        public override XmlSpace XmlSpace => inner.XmlSpace;
        public override string XmlLang => inner.XmlLang;
        public override int AttributeCount => inner.AttributeCount;
        public override bool EOF => inner.EOF;
        public override ReadState ReadState => inner.ReadState;
        public override XmlNameTable NameTable => inner.NameTable;
        public override XmlReaderSettings? Settings => inner.Settings;
        public override string? GetAttribute(string name) => inner.GetAttribute(name);
        public override string? GetAttribute(string name, string? namespaceURI) => inner.GetAttribute(name, namespaceURI);
        public override string GetAttribute(int i) => inner.GetAttribute(i);
        public override bool MoveToAttribute(string name) => inner.MoveToAttribute(name);
        public override bool MoveToAttribute(string name, string? ns) => inner.MoveToAttribute(name, ns);
        public override void MoveToAttribute(int i) => inner.MoveToAttribute(i);
        public override bool MoveToFirstAttribute() => inner.MoveToFirstAttribute();
        public override bool MoveToNextAttribute() => inner.MoveToNextAttribute();
        public override bool MoveToElement() => inner.MoveToElement();
        public override bool ReadAttributeValue() => inner.ReadAttributeValue();
        public override string? LookupNamespace(string prefix) => inner.LookupNamespace(prefix);
        public override void ResolveEntity() => inner.ResolveEntity();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
