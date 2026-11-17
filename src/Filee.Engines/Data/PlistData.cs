// Property lists (.plist, issue #1256): the settings and data files of macOS and iOS apps, in two kinds that are read
// into the data tree like any other format. The XML kind is read by what its elements say (<dict>, <array>, <integer>, ...)
// and not as plain XML; the binary kind ("bplist00") is read by the layout Apple documents in CFBinaryPList.c.
// How a value comes out:
// - dict → object (keys in file order), array and set → array, string → text, true / false, integer → integer of any
//   size, real → float (NaN and the infinities as in XML plists), null (binary only) → null;
// - date → a date as written in RFC 3339 (binary dates count seconds from 2001-01-01 UTC, so they are converted);
// - data → the bytes as Base64 text; a UID (NSKeyedArchiver files) → {"CF$UID": number}, as plutil writes it.
// Files are never trusted: every offset, length and reference of a binary plist is checked before it is used, a value
// that holds itself is refused, and so is a binary file whose shared values would expand to millions of values or nest
// deeper than the data tree allows through any path.

using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Filee.Engines.Data;

internal static partial class PlistData
{
    /// <summary>The key a UID is written under, as <c>plutil -convert json</c> and Python's plistlib do.</summary>
    public const string UidKey = "CF$UID";

    /// <summary>
    /// Most values a binary plist may hold once shared values are counted each time they are used. A binary plist stores
    /// equal values once, so a small file can name a huge tree; this stops that long before it is written out.
    /// </summary>
    internal const long MaxExpandedValues = 5_000_000;

    private static readonly byte[] BinaryMagic = "bplist"u8.ToArray();

    /// <summary>Seconds in a binary date count from this moment.</summary>
    private static readonly DateTime ReferenceDate = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static object? Read(string path) => Read(File.ReadAllBytes(path));

    /// <exception cref="InvalidDataException">The bytes are no property list, or a damaged or hostile one.</exception>
    public static object? Read(byte[] bytes) => bytes.AsSpan().StartsWith(BinaryMagic) ? new BinaryPlist(bytes).Read() : ReadXml(bytes);

    // ───────────────────────── XML ─────────────────────────

    private static object? ReadXml(byte[] bytes)
    {
        var settings = new XmlReaderSettings
        {
            // The DOCTYPE names Apple's DTD on the web: never fetch it.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = 0,
        };
        XDocument document;
        try
        {
            // The reader picks the encoding from the BOM or the declaration, like for any XML file.
            using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"Not a property list: it is neither binary (bplist00) nor valid XML ({ex.Message})", ex);
        }
        if (document.Root is not { Name.LocalName: "plist" } plist)
            throw new InvalidDataException("Not a property list: the XML file has no <plist> root element.");
        var values = plist.Elements().ToList();
        if (values.Count != 1)
            throw new InvalidDataException("The property list must hold exactly one value.");
        return XmlValue(values[0], 0);
    }

    private static object? XmlValue(XElement element, int depth)
    {
        if (depth > DataTree.MaxDepth)
            throw new InvalidDataException("The property list is nested too deeply.");
        var name = element.Name.LocalName;
        switch (name)
        {
            case "dict":
                var map = new DataMap();
                var children = element.Elements().ToList();
                if (children.Count % 2 != 0)
                    throw new InvalidDataException("A <dict> has a <key> without a value.");
                for (var i = 0; i < children.Count; i += 2)
                {
                    if (children[i].Name.LocalName != "key")
                        throw new InvalidDataException($"A <dict> has <{children[i].Name.LocalName}> where a <key> is expected.");
                    map[children[i].Value] = XmlValue(children[i + 1], depth + 1);
                }
                return map;
            case "array":
                return element.Elements().Select(child => XmlValue(child, depth + 1)).ToList();
            case "string":
                return element.Value;
            case "true":
                return true;
            case "false":
                return false;
            case "integer":
                return Integer(element.Value.Trim());
            case "real":
                return Real(element.Value.Trim());
            case "date":
                return Date(element.Value.Trim());
            case "data":
                // Base64 in lines, with the white space of the file's indentation.
                return string.Concat(element.Value.Where(c => !char.IsWhiteSpace(c)));
            default:
                throw new InvalidDataException($"Unknown property list element <{name}>.");
        }
    }

    // Apple's integers fit in 128 bits (a binary plist's widest is 16 bytes): signed from -2^127, unsigned up to 2^128 - 1.
    private static readonly BigInteger MinInteger = -(BigInteger.One << 127);
    private static readonly BigInteger MaxInteger = (BigInteger.One << 128) - 1;

    private static object Integer(string text)
    {
        // Decimal with an optional sign, or hexadecimal after 0x as CoreFoundation and plistlib read it. The length is
        // checked first, so a huge run of digits is refused without being parsed (2^128 has 39 decimal digits).
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var digits = hex ? text[2..] : text;
        if (digits.Length is > 0 and <= 40
            && (hex
                ? digits.All(char.IsAsciiHexDigit) && BigInteger.TryParse("0" + digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var big)
                : BigInteger.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out big))
            && big >= MinInteger && big <= MaxInteger)
        {
            if (big >= long.MinValue && big <= long.MaxValue)
                return (long)big;
            return big;
        }
        throw new InvalidDataException($"\"{Shorten(text)}\" is not an <integer>.");
    }

    // yyyy-MM-ddTHH:mm:ssZ as Apple writes it, and the shortened forms plistlib reads (2020Z, 2020-05Z, 2020-05-17T10Z, ...).
    // ASCII digits only and \z, not $, so nothing can follow the Z, not even a line break.
    [GeneratedRegex(@"^([0-9]{4})(?:-([0-9]{2})(?:-([0-9]{2})(?:T([0-9]{2})(?::([0-9]{2})(?::([0-9]{2}))?)?)?)?)?Z\z", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    /// <summary>A date checked and written in full, since TOML writes a date bare and anything else would land in the file as is.</summary>
    private static DataDate Date(string text)
    {
        if (DatePattern().Match(text) is { Success: true } match)
        {
            string Part(int group, string missing) => match.Groups[group].Success ? match.Groups[group].Value : missing;
            var full = $"{Part(1, "")}-{Part(2, "01")}-{Part(3, "01")}T{Part(4, "00")}:{Part(5, "00")}:{Part(6, "00")}Z";
            if (DateTime.TryParseExact(full, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _))
                return new DataDate(full);
        }
        throw new InvalidDataException($"\"{Shorten(text)}\" is not a <date>.");
    }

    /// <summary>The start of a value for a message, so a huge one doesn't fill it.</summary>
    private static string Shorten(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private static double Real(string text)
    {
        switch (text.ToLowerInvariant())
        {
            case "nan":
                return double.NaN;
            case "inf" or "infinity" or "+inf" or "+infinity":
                return double.PositiveInfinity;
            case "-inf" or "-infinity":
                return double.NegativeInfinity;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new InvalidDataException($"\"{Shorten(text)}\" is not a <real>.");
    }

    // ───────────────────────── Binary ─────────────────────────

    /// <summary>
    /// A "bplist00" file: 8 header bytes, the objects, a table with the offset of each object, and a 32-byte trailer that
    /// says how wide the table's offsets and the references between objects are, how many objects there are and which is
    /// the top one. Objects start with a marker byte: the type in its high four bits, a size or a flag in the low four.
    /// </summary>
    private sealed class BinaryPlist
    {
        private const int HeaderLength = 8;
        private const int TrailerLength = 32;

        private readonly byte[] _data;
        private readonly int _offsetSize;
        private readonly int _referenceSize;
        private readonly int _objectCount;
        private readonly int _topObject;
        private readonly int _tableOffset;

        // Each object is read once, however often others refer to it; that is also how a loop is found.
        private readonly object?[] _objects;
        private readonly long[] _sizes;
        private readonly int[] _heights; // levels below an object, so a shared one is checked for depth wherever it is used again
        private readonly byte[] _state; // 0 not read, 1 being read, 2 read

        public BinaryPlist(byte[] data)
        {
            _data = data;
            if (data.Length < HeaderLength + 1 + TrailerLength)
                throw Damaged("the file is too short");
            if (data[6] != '0' || data[7] != '0')
                throw new InvalidDataException($"Unsupported binary property list version \"{Encoding.ASCII.GetString(data, 6, 2)}\" (only bplist00 is known).");

            var trailer = data.AsSpan(data.Length - TrailerLength);
            _offsetSize = trailer[6];
            _referenceSize = trailer[7];
            var count = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
            var top = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
            var table = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);
            if (_offsetSize is < 1 or > 8 || _referenceSize is < 1 or > 8)
                throw Damaged("its trailer has an invalid offset or reference size");

            // The offset table sits between the objects and the trailer, so it bounds the number of objects by the file size.
            var tableEnd = (ulong)data.Length - TrailerLength;
            if (count == 0 || table < HeaderLength + 1 || table > tableEnd || count > (tableEnd - table) / (ulong)_offsetSize)
                throw Damaged("its offset table does not fit in the file");
            if (top >= count)
                throw Damaged("its top object does not exist");
            _objectCount = (int)count;
            _topObject = (int)top;
            _tableOffset = (int)table;
            _objects = new object?[_objectCount];
            _sizes = new long[_objectCount];
            _heights = new int[_objectCount];
            _state = new byte[_objectCount];
        }

        public object? Read()
        {
            var root = Object(_topObject, 0);
            if (_sizes[_topObject] > MaxExpandedValues)
                throw new InvalidDataException("The property list holds too many values.");
            return root;
        }

        private static InvalidDataException Damaged(string reason) => new($"The binary property list is damaged: {reason}.");

        private object? Object(int index, int depth)
        {
            if (_state[index] == 2)
            {
                if (depth + _heights[index] > DataTree.MaxDepth)
                    throw new InvalidDataException("The property list is nested too deeply.");
                return _objects[index];
            }
            if (_state[index] == 1)
                throw Damaged("a value contains itself");
            if (depth > DataTree.MaxDepth)
                throw new InvalidDataException("The property list is nested too deeply.");

            _state[index] = 1;
            var offset = (ulong)ReadUnsigned(_tableOffset + (long)index * _offsetSize, _offsetSize);
            if (offset < HeaderLength || offset >= (ulong)_tableOffset)
                throw Damaged("an object lies outside the object area");
            var position = (int)offset;
            var marker = _data[position++];
            var (value, size, height) = Value(marker >> 4, marker & 0xF, ref position, depth);
            _objects[index] = value;
            _sizes[index] = size;
            _heights[index] = height;
            _state[index] = 2;
            return value;
        }

        private (object? Value, long Size, int Height) Value(int type, int info, ref int position, int depth)
        {
            switch (type)
            {
                case 0x0:
                    return info switch
                    {
                        0x0 => (null, 1, 0),
                        0x8 => (false, 1, 0),
                        0x9 => (true, 1, 0),
                        _ => throw Damaged($"an object has the unknown marker 0x0{info:X}"),
                    };
                case 0x1:
                    // 1, 2, 4, 8 or 16 bytes.
                    if (info > 4)
                        throw Damaged("an integer has an invalid size");
                    return (ReadInteger(1 << info, ref position, signed: true), 1, 0);
                case 0x2:
                    // 4 or 8 bytes.
                    if (info is not (2 or 3))
                        throw Damaged("a real number has an invalid size");
                    return (ReadReal(1 << info, ref position), 1, 0);
                case 0x3:
                    if (info != 3)
                        throw Damaged("a date is not 8 bytes long");
                    return (ReadDate(Take(8, ref position)), 1, 0);
                case 0x4:
                    return (Convert.ToBase64String(Take(Length(info, ref position), ref position)), 1, 0);
                case 0x5:
                    return (Encoding.Latin1.GetString(Take(Length(info, ref position), ref position)), 1, 0);
                case 0x6:
                    var characters = Length(info, ref position);
                    return (Encoding.BigEndianUnicode.GetString(Take(characters * 2L, ref position)), 1, 0);
                case 0x7:
                    return (Encoding.UTF8.GetString(Take(Length(info, ref position), ref position)), 1, 0);
                case 0x8:
                    var uid = new DataMap { [UidKey] = ReadInteger(info + 1, ref position, signed: false) };
                    return (uid, 2, 1);
                case 0xA or 0xB or 0xC:
                    var list = new List<object?>();
                    var references = Take(Length(info, ref position) * (long)_referenceSize, ref position);
                    var listSize = 1L;
                    var listHeight = 0;
                    for (var at = 0; at < references.Length; at += _referenceSize)
                    {
                        var itemIndex = Reference(references.AsSpan(at, _referenceSize));
                        list.Add(Object(itemIndex, depth + 1));
                        listSize += _sizes[itemIndex];
                        listHeight = Math.Max(listHeight, 1 + _heights[itemIndex]);
                        if (listSize > MaxExpandedValues)
                            throw new InvalidDataException("The property list holds too many values.");
                    }
                    return (list, listSize, listHeight);
                case 0xD:
                    var pairs = Length(info, ref position);
                    var keys = Take(pairs * (long)_referenceSize, ref position);
                    var values = Take(pairs * (long)_referenceSize, ref position);
                    var map = new DataMap();
                    var mapSize = 1L;
                    var mapHeight = 0;
                    for (var i = 0; i < pairs; i++)
                    {
                        var key = Object(Reference(keys.AsSpan(i * _referenceSize, _referenceSize)), depth + 1);
                        if (key is not string name)
                            throw Damaged("a dictionary key is not text");
                        var valueIndex = Reference(values.AsSpan(i * _referenceSize, _referenceSize));
                        map[name] = Object(valueIndex, depth + 1);
                        mapSize += 1 + _sizes[valueIndex];
                        mapHeight = Math.Max(mapHeight, 1 + _heights[valueIndex]);
                        if (mapSize > MaxExpandedValues)
                            throw new InvalidDataException("The property list holds too many values.");
                    }
                    return (map, mapSize, mapHeight);
                default:
                    throw Damaged($"an object has the unknown type 0x{type:X}");
            }
        }

        /// <summary>The count or byte length of an object: in the marker's low bits, or in an integer object after it when those are 0xF.</summary>
        private int Length(int info, ref int position)
        {
            if (info != 0xF)
                return info;
            if (position >= _tableOffset)
                throw Damaged("an object is cut off");
            var marker = _data[position++];
            if (marker >> 4 != 0x1)
                throw Damaged("a length is not an integer");
            var size = 1 << (marker & 0xF);
            if (size > 8)
                throw Damaged("a length is too wide");
            var length = ReadUnsigned(position, size);
            position += size;
            if (length > _data.Length)
                throw Damaged("an object is longer than the file");
            return (int)length;
        }

        /// <summary>The next <paramref name="count"/> bytes of the object area, refusing to run past it.</summary>
        private byte[] Take(long count, ref int position)
        {
            if (count < 0 || position < 0 || count > _tableOffset - position)
                throw Damaged("an object is cut off");
            var bytes = _data.AsSpan(position, (int)count).ToArray();
            position += (int)count;
            return bytes;
        }

        private int Reference(ReadOnlySpan<byte> bytes)
        {
            ulong value = 0;
            foreach (var b in bytes)
                value = (value << 8) | b;
            if (value >= (ulong)_objectCount)
                throw Damaged("an object refers to one that does not exist");
            return (int)value;
        }

        private long ReadUnsigned(long position, int size)
        {
            if (position < 0 || position + size > _data.Length)
                throw Damaged("an object is cut off");
            ulong value = 0;
            for (var i = 0; i < size; i++)
                value = (value << 8) | _data[position + i];
            if (value > long.MaxValue)
                throw Damaged("a number is too large for its place");
            return (long)value;
        }

        private object ReadInteger(int size, ref int position, bool signed)
        {
            var bytes = Take(size, ref position);
            // 1, 2 and 4 bytes are unsigned, 8 bytes are signed, and 16 bytes are a signed 128-bit number.
            var negative = signed && size >= 8;
            var big = new BigInteger(bytes, isUnsigned: !negative, isBigEndian: true);
            // Two statements: in one conditional the long would be widened to a BigInteger.
            if (big >= long.MinValue && big <= long.MaxValue)
                return (long)big;
            return big;
        }

        private object ReadReal(int size, ref int position)
        {
            if (size is not (4 or 8))
                throw Damaged("a real number has an invalid size");
            var bytes = Take(size, ref position);
            return size == 4 ? BinaryPrimitives.ReadSingleBigEndian(bytes) : BinaryPrimitives.ReadDoubleBigEndian(bytes);
        }

        private static object ReadDate(byte[] bytes)
        {
            var seconds = BinaryPrimitives.ReadDoubleBigEndian(bytes);
            // Beyond the years 1 to 9999, or not a number at all, the number itself is all there is.
            if (!double.IsFinite(seconds) || seconds < -6.3e10 || seconds > 2.5e11)
                return seconds;
            DateTime date;
            try
            {
                date = ReferenceDate.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
            }
            catch (ArgumentOutOfRangeException)
            {
                return seconds;
            }
            return new DataDate(date.ToString(date.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
        }
    }
}
