// The ZIP package of a document (HWPX, DOCX, OFD), read within DocumentLimits: parts are looked up by name inside the
// package only, every part is counted while it is unpacked (its own size, the package's total and the inflate ratio),
// XML is read by SafeXml without DTDs or a resolver, and parts are only ever written out under names Filee makes.
// A damaged package fails with InvalidDataException, one that passes a limit with error.document_too_complex.

using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Documents;

/// <summary>A document package opened for reading.</summary>
internal sealed class DocumentPackage : IDisposable
{
    private readonly ZipArchive _zip;
    private long _total;
    private int _elements;

    private DocumentPackage(ZipArchive zip, DocumentLimits limits)
    {
        _zip = zip;
        Limits = limits;
    }

    public DocumentLimits Limits { get; }

    /// <summary>Bytes unpacked so far.</summary>
    public long BytesRead => _total;

    /// <summary>XML elements read so far.</summary>
    public int ElementsRead => _elements;

    public IReadOnlyCollection<ZipArchiveEntry> Entries => _zip.Entries;

    /// <summary>Opens the package at <paramref name="path"/>; <paramref name="notAPackage"/> makes the error for a file that is no ZIP.</summary>
    public static DocumentPackage Open(string path, DocumentLimits? limits = null, Func<Exception>? notAPackage = null) =>
        Open(() => ZipFile.OpenRead(path), limits ?? DocumentLimits.Default, notAPackage);

    /// <summary>A package in a stream (left open).</summary>
    public static DocumentPackage Open(Stream stream, DocumentLimits? limits = null, Func<Exception>? notAPackage = null) =>
        Open(() => new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true), limits ?? DocumentLimits.Default, notAPackage);

    private static DocumentPackage Open(Func<ZipArchive> open, DocumentLimits limits, Func<Exception>? notAPackage)
    {
        ZipArchive zip;
        try
        {
            zip = open();
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or NotSupportedException
                                   || ex is IOException and not (FileNotFoundException or DirectoryNotFoundException))
        {
            throw notAPackage?.Invoke() ?? new InvalidDataException("The file is not a ZIP package. It may be damaged.", ex);
        }
        try
        {
            // The central directory is read when the entries are first listed.
            if (zip.Entries.Count > limits.MaxPackageEntries)
                throw DocumentLimits.Exceeded($"The package has more than {DocumentLimits.Count(limits.MaxPackageEntries)} parts.");
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or IOException)
        {
            zip.Dispose();
            throw notAPackage?.Invoke() ?? new InvalidDataException("The file is not a ZIP package. It may be damaged.", ex);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
        return new DocumentPackage(zip, limits);
    }

    public void Dispose() => _zip.Dispose();

    /// <summary>The entry of that exact name; null when there is none.</summary>
    public ZipArchiveEntry? GetEntry(string name)
    {
        try
        {
            return name.Length == 0 ? null : _zip.GetEntry(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The unpacked bytes of <paramref name="entry"/>, counted against the part, package and ratio limits. A part whose
    /// header already claims too much is refused before it is unpacked.
    /// </summary>
    public Stream Open(ZipArchiveEntry entry)
    {
        var name = entry.FullName;
        if (entry.Length > Limits.MaxPartBytes)
            throw PartTooLarge(name);
        if (_total + entry.Length > Limits.MaxPackageBytes)
            throw PackageTooLarge();
        Stream inner;
        try
        {
            inner = entry.Open();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            throw new InvalidDataException($"The part {name} of the package can't be unpacked ({ex.Message}).", ex);
        }
        var compressed = Math.Max(1, entry.CompressedLength);
        long counted = 0;
        return new LimitedStream(new DamageStream(inner, name), Limits.MaxPartBytes, () => PartTooLarge(name), read =>
        {
            _total += read - counted;
            counted = read;
            if (_total > Limits.MaxPackageBytes)
                throw PackageTooLarge();
            if (read > Limits.InflateRatioFloor && read / compressed > Limits.MaxInflateRatio)
                throw DocumentLimits.Exceeded($"The part {name} unpacks to more than {Limits.MaxInflateRatio} times its packed size.");
        });
    }

    /// <summary>
    /// An XML part read by <see cref="SafeXml"/>; its elements count against the package's. Throws
    /// <see cref="XmlException"/> for a part that is not well-formed XML (callers say which part of which format).
    /// </summary>
    public XDocument LoadXml(ZipArchiveEntry entry, bool preserveWhitespace = true)
    {
        using var stream = Open(entry);
        try
        {
            var document = SafeXml.Load(stream, Limits.Xml(_elements, preserveWhitespace),
                message => DocumentLimits.Exceeded($"{entry.FullName}: {message}"), out var elements);
            _elements += elements;
            return document;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            throw new XmlException(ex.Message, ex);
        }
    }

    /// <summary>The bytes of a part; null when it holds more than <paramref name="max"/> (the part is then left out).</summary>
    public byte[]? ReadBytes(ZipArchiveEntry entry, long max)
    {
        if (entry.Length > max)
            return null;
        using var stream = Open(entry);
        using var copy = new MemoryStream((int)Math.Min(entry.Length, 1024 * 1024));
        var buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (copy.Length + count > max)
                return null;
            copy.Write(buffer, 0, count);
        }
        return copy.ToArray();
    }

    /// <summary>
    /// Unpacks a picture into a new file at <paramref name="path"/>. Returns false (and leaves no file) when its
    /// header claims more than <see cref="DocumentLimits.MaxPicturePixels"/>. With <paramref name="unpack"/> it also
    /// counts against what the document's pictures may unpack to in all, refusing the document past it.
    /// </summary>
    public bool ExtractPicture(ZipArchiveEntry entry, string path, UnpackLimit? unpack = null)
    {
        using (var source = Open(entry))
        {
            if (unpack is not null)
                unpack.Extract(entry, source, path);
            else
            {
                using var target = File.Create(path);
                source.CopyTo(target);
            }
        }
        if (PictureSize.Read(path) is { } size && (long)size.Width * size.Height > Limits.MaxPicturePixels)
        {
            File.Delete(path);
            return false;
        }
        return true;
    }

    /// <summary>
    /// The extension of a part name for a file Filee writes: "." and up to 8 ASCII letters or digits, else "" (a name
    /// in a hostile package may hold anything, ":stream" included).
    /// </summary>
    public static string SafeExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0 || name.Length - dot - 1 is < 1 or > 8 || name.AsSpan(dot + 1).ContainsAnyExcept(AsciiLettersAndDigits))
            return "";
        return name[dot..].ToLowerInvariant();
    }

    private static readonly System.Buffers.SearchValues<char> AsciiLettersAndDigits =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789");

    /// <summary>Reads a part; a package cut short or damaged inside a part is invalid data, not a failing disk.</summary>
    private sealed class DamageStream(Stream inner, string name) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                return inner.Read(buffer);
            }
            catch (IOException ex)
            {
                throw new InvalidDataException($"The part {name} of the package is damaged ({ex.Message}).", ex);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private Exception PartTooLarge(string name) =>
        DocumentLimits.Exceeded($"The part {name} unpacks to more than {DocumentLimits.Megabytes(Limits.MaxPartBytes)}.");

    private Exception PackageTooLarge() =>
        DocumentLimits.Exceeded($"The package unpacks to more than {DocumentLimits.Megabytes(Limits.MaxPackageBytes)}.");
}
