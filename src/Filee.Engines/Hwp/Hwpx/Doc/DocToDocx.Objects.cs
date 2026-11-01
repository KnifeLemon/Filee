// DOC pictures, shapes and text boxes → DrawingML.
//
//  * Inline pictures: a special character 1 whose sprmCPicLocation points into the Data stream at a PICF (size, scale,
//    crop) followed by an OfficeArt shape container and the BLIPs it uses (OfficeArtFBSE records, each with its
//    picture). Older pictures hold a metafile right after the PICF. An OLE object's picture is that of its EMBED
//    field's result, written as w:object so the renderer's check sees the OLE object.
//  * Floating objects: a special character 8 at a CP of PlcSpaMom (PlcSpaHdr in headers): its SPA gives the shape id,
//    the rectangle, what the position is relative to and the wrapping; the shape itself (type, properties, the BLIP
//    it shows from the BStore, its text box) is in the OfficeArt drawing of the table stream (fcDggInfo).
//  * Text boxes: their text is a story of the text box document, found by the shape id in PlcftxbxTxt.
//
// BLIPs: JPEG, PNG and TIFF are copied, DIBs get a BMP file header, EMF and WMF are inflated when compressed and a WMF
// gets its placeable header. Records follow [MS-ODRAW]; every length is checked against what holds it.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx.Doc;

internal sealed partial class DocToDocx
{
    private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private const int EmuPerTwip = 635;

    /// <summary>A shape of the OfficeArt drawings: its type, simple properties (pid → value) and flags.</summary>
    private sealed class Shape
    {
        public int Type;
        public uint Flags;
        public Dictionary<int, uint> Properties { get; } = [];
        public int? TextId;
    }

    private readonly Dictionary<int, Shape> _shapes = [];

    /// <summary>The BStore: where each BLIP is (an FBSE record in the table stream).</summary>
    private readonly List<(int Offset, int Length)> _bstore = [];

    private Dictionary<int, (int Start, int End)>? _textBoxes, _headerTextBoxes;

    /// <summary>Text boxes being written (a text box can't hold its own story again).</summary>
    private int _inTextBox;

    private readonly HashSet<int> _textBoxesWritten = [];

    // ───────────────────────── OfficeArt records ─────────────────────────

    private readonly record struct Record(int Instance, int Type, int Start, int Length)
    {
        public int End => Start + Length;
    }

    /// <summary>The record at <paramref name="at"/> of <paramref name="bytes"/> (header 8 bytes), when it fits within <paramref name="end"/>.</summary>
    private static Record? RecordAt(ReadOnlySpan<byte> bytes, int at, int end)
    {
        if (at < 0 || at + 8 > end || end > bytes.Length)
            return null;
        var instance = BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]) >> 4;
        var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 2)..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);
        if (length > end - at - 8)
            return null;
        return new Record(instance, type, at + 8, (int)length);
    }

    /// <summary>The children of a container record.</summary>
    private static List<Record> Children(ReadOnlySpan<byte> bytes, Record container)
    {
        var children = new List<Record>();
        var at = container.Start;
        while (RecordAt(bytes, at, container.End) is { } child)
        {
            children.Add(child);
            at = child.End;
        }
        return children;
    }

    /// <summary>
    /// OfficeArtContent: the drawing group (with the BStore) and then, per drawing, a dgglbl byte (0 main text,
    /// 1 headers) and its DgContainer, whose group containers hold the shapes. Groups are walked with a stack.
    /// </summary>
    private void ReadDrawings()
    {
        var content = _doc.Blob(DocFile.FibIndex.DggInfo);
        if (content.Length == 0)
            return;
        var (offset, _) = _doc.Location(DocFile.FibIndex.DggInfo);
        var table = _doc.Table;
        var end = offset + content.Length;
        if (RecordAt(table, offset, end) is not { Type: 0xF000 } group)
            return;
        foreach (var child in Children(table, group).Where(c => c.Type == 0xF001))
        {
            foreach (var fbse in Children(table, child))
                _bstore.Add((fbse.Start - 8, fbse.Length + 8));
        }
        var at = group.End;
        var stack = new Stack<Record>();
        while (at + 1 < end && RecordAt(table, at + 1, end) is { Type: 0xF002 } drawing)
        {
            stack.Push(drawing);
            at = drawing.End;
        }
        var visited = 0;
        while (stack.Count > 0)
        {
            var record = stack.Pop();
            if (++visited > _doc.Limits.MaxObjects)
                throw Documents.DocumentLimits.Exceeded("The document has too many drawing objects.");
            foreach (var child in Children(table, record))
            {
                if (child.Type is 0xF002 or 0xF003)
                    stack.Push(child);
                else if (child.Type == 0xF004)
                    ReadShape(table, child);
            }
        }
    }

    private void ReadShape(byte[] table, Record container)
    {
        var shape = new Shape();
        int? id = null;
        foreach (var child in Children(table, container))
        {
            switch (child.Type)
            {
                case 0xF003: // a group as the first record of its container: its shapes are read as the stack reaches them
                    break;
                case 0xF00A when child.Length >= 8:
                    shape.Type = child.Instance;
                    id = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(child.Start));
                    shape.Flags = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(child.Start + 4));
                    break;
                case 0xF00B or 0xF121:
                    ReadProperties(table, child, shape.Properties);
                    break;
                case 0xF122:
                    // The tertiary FOPT holds other flags under the same property ids: the primary ones stay.
                    ReadProperties(table, child, shape.Properties, overwrite: false);
                    break;
                case 0xF00D when child.Length >= 4:
                    shape.TextId = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(child.Start));
                    break;
            }
        }
        if (id is { } spid)
            _shapes.TryAdd(spid, shape);
    }

    /// <summary>An FOPT: instance = number of properties, each an opid (pid, fBid, fComplex) and a 4-byte value.</summary>
    private static void ReadProperties(ReadOnlySpan<byte> bytes, Record fopt, Dictionary<int, uint> properties, bool overwrite = true)
    {
        for (var i = 0; i < fopt.Instance && fopt.Start + i * 6 + 6 <= fopt.End; i++)
        {
            var opid = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(fopt.Start + i * 6)..]);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(fopt.Start + i * 6 + 2)..]);
            if ((opid & 0x8000) == 0 && (overwrite || !properties.ContainsKey(opid & 0x3FFF)))
                properties[opid & 0x3FFF] = value;
        }
    }

    // ───────────────────────── BLIPs ─────────────────────────

    /// <summary>The picture of an FBSE record at <paramref name="fbse"/> of <paramref name="bytes"/>: its own BLIP, or the one at foDelay in the WordDocument stream.</summary>
    private (byte[] Bytes, string Extension)? FbsePicture(ReadOnlySpan<byte> bytes, Record fbse)
    {
        if (fbse.Type != 0xF007 || fbse.Length < 36)
            return null;
        var cbName = bytes[fbse.Start + 33];
        var blipAt = fbse.Start + 36 + cbName;
        if (RecordAt(bytes, blipAt, fbse.End) is { } embedded)
            return Blip(bytes, embedded);
        var delay = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(fbse.Start + 28)..]);
        if (delay < _doc.Word.Length && RecordAt(_doc.Word, (int)delay, _doc.Word.Length) is { } stored)
            return Blip(_doc.Word, stored);
        return null;
    }

    /// <summary>A BLIP record's picture: one UID (16 bytes) or two when the instance is odd, then the data.</summary>
    private (byte[] Bytes, string Extension)? Blip(ReadOnlySpan<byte> bytes, Record blip)
    {
        var uids = (blip.Instance & 1) == 1 ? 32 : 16;
        switch (blip.Type)
        {
            case 0xF01A or 0xF01B:
                {
                    // Metafile header: uncompressed size, bounds, size in EMU, saved size, compression (0 deflate, 0xFE none).
                    var header = blip.Start + uids;
                    if (header + 34 > blip.End)
                        return null;
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[header..]);
                    var saved = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(header + 28)..]);
                    var compressed = bytes[header + 32] == 0;
                    var data = header + 34;
                    var length = (int)Math.Min(saved, (uint)(blip.End - data));
                    var metafile = compressed ? Inflate(bytes.Slice(data, length), size) : bytes.Slice(data, length).ToArray();
                    if (metafile is null)
                        return null;
                    if (blip.Type == 0xF01A)
                        return (metafile, "emf");
                    var bounds = bytes.Slice(header + 4, 16);
                    var emu = (BinaryPrimitives.ReadInt32LittleEndian(bytes[(header + 20)..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[(header + 24)..]));
                    return (Placeable(metafile, bounds, emu), "wmf");
                }
            case 0xF01D or 0xF02A or 0xF01E or 0xF01F or 0xF029:
                {
                    var data = blip.Start + uids + 1; // the tag byte
                    if (data > blip.End)
                        return null;
                    var picture = bytes[data..blip.End].ToArray();
                    return blip.Type switch
                    {
                        0xF01E => (picture, "png"),
                        0xF029 => (picture, "tif"),
                        0xF01F => Bitmap(picture) is { } bmp ? (bmp, "bmp") : null,
                        _ => (picture, "jpg"),
                    };
                }
            default:
                return null; // PICT and unknown kinds
        }
    }

    /// <summary>Inflates a zlib stream, at most to the size the header gives and the part limit.</summary>
    private byte[]? Inflate(ReadOnlySpan<byte> data, uint size)
    {
        var limit = (int)Math.Min(Math.Min(size, (uint)int.MaxValue), _doc.Limits.MaxPartBytes);
        try
        {
            using var input = new MemoryStream(data.ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var output = new byte[limit];
            var read = zlib.ReadAtLeast(output, limit, throwOnEndOfStream: false);
            return read == limit ? output : output[..read];
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A WMF with the placeable header (bounds in metafile units, units per inch from the size in EMU) it lacks in DOC.</summary>
    private static byte[] Placeable(byte[] wmf, ReadOnlySpan<byte> bounds, (int Cx, int Cy) emu)
    {
        if (wmf.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(wmf) == 0x9AC6CDD7)
            return wmf;
        var left = BinaryPrimitives.ReadInt32LittleEndian(bounds);
        var top = BinaryPrimitives.ReadInt32LittleEndian(bounds[4..]);
        var right = BinaryPrimitives.ReadInt32LittleEndian(bounds[8..]);
        var bottom = BinaryPrimitives.ReadInt32LittleEndian(bounds[12..]);
        var width = (long)right - left;
        var inch = width > 0 && emu.Cx > 0 ? Math.Clamp(width * 914400 / emu.Cx, 1, 65535) : 1440;
        var header = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x9AC6CDD7);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(6), (short)Math.Clamp(left, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(8), (short)Math.Clamp(top, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(10), (short)Math.Clamp(right, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(12), (short)Math.Clamp(bottom, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)inch);
        ushort checksum = 0;
        for (var i = 0; i < 20; i += 2)
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(i));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), checksum);
        return [.. header, .. wmf];
    }

    /// <summary>A DIB with the BMP file header in front: where the pixels start after the header and palette.</summary>
    private static byte[]? Bitmap(byte[] dib)
    {
        if (dib.Length < 40)
            return null;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize < 12 || headerSize > dib.Length)
            return null;
        var bits = headerSize >= 16 ? BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14)) : 0;
        var compression = headerSize >= 20 ? BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16)) : 0;
        var used = headerSize >= 36 ? BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32)) : 0;
        var colors = used > 0 ? used : bits <= 8 ? 1 << bits : 0;
        var masks = compression == 3 && headerSize == 40 ? 12 : 0;
        var offset = 14L + headerSize + masks + Math.Min(colors, 256) * 4L;
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), (int)Math.Min(offset, file.Length));
        dib.CopyTo(file, 14);
        return file;
    }

    // ───────────────────────── Inline pictures ─────────────────────────

    /// <summary>
    /// The picture of a character 1: PICF (lcb, cbHeader, mfpf.mm, ..., dxaGoal and dyaGoal at 28, mx and my at 32,
    /// crops at 36), then the picture's OfficeArt records (mm 0x64; 0x66 adds a file name first) or a metafile.
    /// </summary>
    private XElement? InlinePicture(RunInfo info)
    {
        var data = _doc.Data;
        if (info.PicLocation < 0 || info.PicLocation > data.Length - 68L)
            return null;
        var at = info.PicLocation;
        var lcb = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
        var cbHeader = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4));
        if (lcb < cbHeader || cbHeader < 68 || lcb > data.Length - at)
            return null;
        _budget.Object();
        var picf = data.AsSpan(at, cbHeader);
        var mm = BinaryPrimitives.ReadInt16LittleEndian(picf[6..]);
        var dxaGoal = BinaryPrimitives.ReadInt16LittleEndian(picf[28..]);
        var dyaGoal = BinaryPrimitives.ReadInt16LittleEndian(picf[30..]);
        var mx = BinaryPrimitives.ReadUInt16LittleEndian(picf[32..]);
        var my = BinaryPrimitives.ReadUInt16LittleEndian(picf[34..]);
        var cropLeft = BinaryPrimitives.ReadInt16LittleEndian(picf[36..]);
        var cropTop = BinaryPrimitives.ReadInt16LittleEndian(picf[38..]);
        var cropRight = BinaryPrimitives.ReadInt16LittleEndian(picf[40..]);
        var cropBottom = BinaryPrimitives.ReadInt16LittleEndian(picf[42..]);
        var start = at + cbHeader;
        var end = at + lcb;

        (byte[] Bytes, string Extension)? picture = null;
        Dictionary<int, uint> properties = [];
        if (mm is 0x64 or 0x66)
        {
            if (mm == 0x66 && start < end)
                start += 1 + data[start];
            if (RecordAt(data, start, end) is { Type: 0xF004 } container)
            {
                foreach (var child in Children(data, container).Where(c => c.Type is 0xF00B or 0xF122))
                    ReadProperties(data, child, properties, overwrite: child.Type == 0xF00B);
                // The FBSEs of the pictures follow the shape container; pib (property 260) picks one, 1-based.
                var fbses = new List<Record>();
                var next = container.End;
                while (RecordAt(data, next, end) is { } fbse)
                {
                    fbses.Add(fbse);
                    next = fbse.End;
                }
                var pib = (int)properties.GetValueOrDefault(0x0104, 1u);
                if (pib >= 1 && pib <= fbses.Count)
                    picture = FbsePicture(data, fbses[pib - 1]);
                else if (fbses.Count > 0)
                    picture = FbsePicture(data, fbses[0]);
            }
        }
        else if (end > start)
        {
            // An older picture: a metafile (without its placeable header) right after the PICF.
            var bounds = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(bounds.AsSpan(8), Math.Max(1, (int)BinaryPrimitives.ReadInt16LittleEndian(picf[8..])));
            BinaryPrimitives.WriteInt32LittleEndian(bounds.AsSpan(12), Math.Max(1, (int)BinaryPrimitives.ReadInt16LittleEndian(picf[10..])));
            picture = (Placeable(data[start..end], bounds, (dxaGoal * EmuPerTwip, dyaGoal * EmuPerTwip)), "wmf");
        }
        if (picture is not { } found)
            return null;

        // The picture as placed: its goal size less the crops, scaled by mx and my (per mille).
        var width = (long)Math.Max(1, dxaGoal - cropLeft - cropRight) * (mx == 0 ? 1000 : mx) / 1000;
        var height = (long)Math.Max(1, dyaGoal - cropTop - cropBottom) * (my == 0 ? 1000 : my) / 1000;
        var id = AddMedia(found.Bytes, found.Extension);
        if (info.Ole2)
        {
            // An OLE object's picture, as Word writes one in DOCX.
            return new XElement(W + "object",
                new XElement(V + "shape", new XAttribute("style", $"width:{width / 20.0:0.##}pt;height:{height / 20.0:0.##}pt".Replace(',', '.')),
                    new XElement(V + "imagedata", new XAttribute(R + "id", id))),
                new XElement(O + "OLEObject", new XAttribute("Type", "Embed")));
        }
        return new XElement(W + "drawing", new XElement(Wp + "inline",
            new XElement(Wp + "extent", new XAttribute("cx", width * EmuPerTwip), new XAttribute("cy", height * EmuPerTwip)),
            DocPr(),
            PictureGraphic(id, CropOf(properties))));
    }

    private XElement DocPr() => new(Wp + "docPr", new XAttribute("id", ++_drawingId), new XAttribute("name", $"Picture {_drawingId}"));

    /// <summary>a:srcRect from the shape's crop properties (16.16 fractions of the picture), or null.</summary>
    private static XElement? CropOf(Dictionary<int, uint> properties)
    {
        int Crop(int pid) => properties.TryGetValue(pid, out var value) ? (int)Math.Clamp((int)value / 65536.0 * 100000, -100000, 100000) : 0;
        var (top, bottom, left, right) = (Crop(0x0100), Crop(0x0101), Crop(0x0102), Crop(0x0103));
        if ((top | bottom | left | right) == 0)
            return null;
        return new XElement(A + "srcRect", new XAttribute("l", left), new XAttribute("t", top), new XAttribute("r", right), new XAttribute("b", bottom));
    }

    private static XElement PictureGraphic(string id, XElement? crop) =>
        new(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", PictureUri),
            new XElement(Pic + "pic",
                new XElement(Pic + "nvPicPr", new XElement(Pic + "cNvPr", new XAttribute("id", 0), new XAttribute("name", "")), new XElement(Pic + "cNvPicPr")),
                new XElement(Pic + "blipFill", new XElement(A + "blip", new XAttribute(R + "embed", id)), crop, new XElement(A + "stretch", new XElement(A + "fillRect"))),
                new XElement(Pic + "spPr", new XElement(A + "prstGeom", new XAttribute("prst", "rect"))))));

    // ───────────────────────── Floating objects ─────────────────────────

    /// <summary>The SPAs of the main text or of the headers: CP → (shape id, rectangle, flags).</summary>
    private Dictionary<int, (int Id, int Left, int Top, int Right, int Bottom, ushort Flags)>? _anchors, _headerAnchors;

    private static Dictionary<int, (int, int, int, int, int, ushort)> Anchors(ReadOnlySpan<byte> plc)
    {
        var anchors = new Dictionary<int, (int, int, int, int, int, ushort)>();
        var count = Plc.Count(plc.Length, 26);
        for (var i = 0; i < count; i++)
        {
            var spa = Plc.Data(plc, count, 26, i);
            anchors.TryAdd(Plc.Cp(plc, i), (BinaryPrimitives.ReadInt32LittleEndian(spa), BinaryPrimitives.ReadInt32LittleEndian(spa[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(spa[8..]), BinaryPrimitives.ReadInt32LittleEndian(spa[12..]),
                BinaryPrimitives.ReadInt32LittleEndian(spa[16..]), BinaryPrimitives.ReadUInt16LittleEndian(spa[20..])));
        }
        return anchors;
    }

    /// <summary>The drawing of a character 8 at <paramref name="cp"/>: a floating picture, shape or text box.</summary>
    private XElement? FloatingObject(int cp)
    {
        Dictionary<int, (int Id, int Left, int Top, int Right, int Bottom, ushort Flags)> anchors;
        int relative;
        if (_inHeader)
        {
            anchors = _headerAnchors ??= Anchors(_doc.Blob(DocFile.FibIndex.PlcSpaHdr));
            relative = cp - _doc.HeaderStart;
        }
        else
        {
            anchors = _anchors ??= Anchors(_doc.Blob(DocFile.FibIndex.PlcSpaMom));
            relative = cp;
        }
        if (!anchors.TryGetValue(relative, out var spa) || !_shapes.TryGetValue(spa.Id, out var shape))
            return null;
        _budget.Object();
        var width = (long)Math.Clamp(spa.Right - spa.Left, 1, 31_680) * EmuPerTwip;
        var height = (long)Math.Clamp(spa.Bottom - spa.Top, 1, 31_680) * EmuPerTwip;

        XElement? graphic = null;
        if (shape.Properties.TryGetValue(0x0104, out var pib) && pib >= 1 && pib <= _bstore.Count)
        {
            var (offset, length) = _bstore[(int)pib - 1];
            if (RecordAt(_doc.Table, offset, offset + length) is { } fbse && FbsePicture(_doc.Table, fbse) is { } picture)
                graphic = PictureGraphic(AddMedia(picture.Bytes, picture.Extension), CropOf(shape.Properties));
        }
        if (graphic is null && ShapeGraphic(spa.Id, shape, width, height) is { } drawn)
            graphic = drawn;
        if (graphic is null)
            return null;

        // SPA flags: fHdr, bx (bits 1–2: margin, page, column), by (3–4: margin, page, paragraph), wr (5–8), fBelowText (14).
        var bx = (spa.Flags >> 1) & 3;
        var by = (spa.Flags >> 3) & 3;
        var wrap = (spa.Flags >> 5) & 15;
        var below = (spa.Flags & 0x4000) != 0;
        // Word 2000 and later also give the position as shape properties: posh / posv (0 at an offset, 1 left / top,
        // 2 centre, 3 right / bottom, 4 inside, 5 outside) relative to posrelh / posrelv (0 margin, 1 page, 2 column /
        // paragraph, 3 character / line); and fBehindDocument (bit 5 of the group shape's flags, used when bit 21 says so).
        var horizontalFrom = bx switch { 1 => "page", 2 => "column", _ => "margin" };
        var verticalFrom = by switch { 1 => "page", 2 => "paragraph", _ => "margin" };
        string? horizontalAlign = null, verticalAlign = null;
        if (shape.Properties.TryGetValue(0x0390, out var relativeH))
            horizontalFrom = relativeH switch { 0 => "margin", 1 => "page", 3 => "character", _ => "column" };
        if (shape.Properties.TryGetValue(0x0392, out var relativeV))
            verticalFrom = relativeV switch { 0 => "margin", 1 => "page", 3 => "line", _ => "paragraph" };
        if (shape.Properties.TryGetValue(0x038F, out var posh))
            horizontalAlign = posh switch { 1 => "left", 2 => "center", 3 => "right", 4 => "inside", 5 => "outside", _ => null };
        if (shape.Properties.TryGetValue(0x0391, out var posv))
            verticalAlign = posv switch { 1 => "top", 2 => "center", 3 => "bottom", 4 => "inside", 5 => "outside", _ => null };
        if (shape.Properties.TryGetValue(0x03BF, out var group) && (group & (1u << 21)) != 0)
            below = (group & (1u << 5)) != 0;
        var wrapElement = wrap switch
        {
            1 => new XElement(Wp + "wrapTopAndBottom"),
            3 => new XElement(Wp + "wrapNone"),
            _ => new XElement(Wp + "wrapSquare", new XAttribute("wrapText", "bothSides")),
        };
        return new XElement(W + "drawing", new XElement(Wp + "anchor",
            new XAttribute("distT", 0), new XAttribute("distB", 0), new XAttribute("distL", 114300), new XAttribute("distR", 114300),
            new XAttribute("simplePos", 0), new XAttribute("relativeHeight", _drawingId + 1), new XAttribute("behindDoc", below ? 1 : 0),
            new XAttribute("locked", 0), new XAttribute("layoutInCell", 1), new XAttribute("allowOverlap", 1),
            new XElement(Wp + "simplePos", new XAttribute("x", 0), new XAttribute("y", 0)),
            new XElement(Wp + "positionH", new XAttribute("relativeFrom", horizontalFrom),
                horizontalAlign is null ? new XElement(Wp + "posOffset", (long)spa.Left * EmuPerTwip) : new XElement(Wp + "align", horizontalAlign)),
            new XElement(Wp + "positionV", new XAttribute("relativeFrom", verticalFrom),
                verticalAlign is null ? new XElement(Wp + "posOffset", (long)spa.Top * EmuPerTwip) : new XElement(Wp + "align", verticalAlign)),
            new XElement(Wp + "extent", new XAttribute("cx", width), new XAttribute("cy", height)),
            wrapElement,
            DocPr(),
            graphic));
    }

    /// <summary>
    /// A shape without a picture: a text box (any shape with text) or a rectangle, rounded rectangle, ellipse or line,
    /// with its fill (fillColor, fFilled) and outline (lineColor, lineWidth, fLine); null for other shapes.
    /// </summary>
    private XElement? ShapeGraphic(int id, Shape shape, long width, long height)
    {
        var geometry = shape.Type switch
        {
            1 or 202 => "rect",
            2 => "roundRect",
            3 => "ellipse",
            20 => "line",
            _ => null,
        };
        var story = TextBoxStory(id, shape);
        if (geometry is null && story is null)
            return null;
        var properties = shape.Properties;
        bool Flag(int pid, int bit, bool fallback) =>
            properties.TryGetValue(pid, out var value) && (value & (1u << (bit + 16))) != 0 ? (value & (1u << bit)) != 0 : fallback;
        string? Color(int pid) => properties.TryGetValue(pid, out var value) && (value >> 24) == 0
            ? $"{value & 0xFF:X2}{(value >> 8) & 0xFF:X2}{(value >> 16) & 0xFF:X2}" : null;
        var spPr = new XElement(Wps + "spPr",
            new XElement(A + "xfrm",
                (shape.Flags & 0x40) != 0 ? new XAttribute("flipH", 1) : null, (shape.Flags & 0x80) != 0 ? new XAttribute("flipV", 1) : null,
                new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                new XElement(A + "ext", new XAttribute("cx", width), new XAttribute("cy", height))),
            new XElement(A + "prstGeom", new XAttribute("prst", geometry ?? "rect")));
        if (geometry != "line")
        {
            spPr.Add(Flag(0x01BF, 4, true)
                ? new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", Color(0x0181) ?? "FFFFFF")))
                : new XElement(A + "noFill"));
        }
        spPr.Add(Flag(0x01FF, 3, true)
            ? new XElement(A + "ln", new XAttribute("w", properties.GetValueOrDefault(0x01CB, 9525u)),
                new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", Color(0x01C0) ?? "000000"))))
            : new XElement(A + "ln", new XElement(A + "noFill")));
        var wsp = new XElement(Wps + "wsp", new XElement(Wps + "cNvSpPr", story is null ? null : new XAttribute("txBox", 1)), spPr);
        if (story is { } range)
        {
            _inTextBox++;
            try
            {
                using var level = _budget.Enter();
                wsp.Add(new XElement(Wps + "txbx", new XElement(W + "txbxContent", Story(range.Start, range.End, terminated: true))));
            }
            finally
            {
                _inTextBox--;
            }
        }
        // Text insets in EMU (dxTextLeft, dyTextTop, dxTextRight, dyTextBottom), Word's defaults 0.1" and 0.05".
        wsp.Add(new XElement(Wps + "bodyPr",
            new XAttribute("lIns", properties.GetValueOrDefault(0x0081, 91440u)), new XAttribute("tIns", properties.GetValueOrDefault(0x0082, 45720u)),
            new XAttribute("rIns", properties.GetValueOrDefault(0x0083, 91440u)), new XAttribute("bIns", properties.GetValueOrDefault(0x0084, 45720u))));
        return new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", ShapeUri), wsp));
    }

    /// <summary>
    /// The CPs of a text box's story: PlcftxbxTxt (PlcfHdrtxbxTxt in headers) has a CP per story and an FTXBXS whose
    /// lid is the shape id. Each story is written once.
    /// </summary>
    private (int Start, int End)? TextBoxStory(int id, Shape shape)
    {
        if (shape.TextId is null || _inTextBox > 0)
            return null;
        var stories = _inHeader
            ? _headerTextBoxes ??= TextBoxStories(_doc.Blob(DocFile.FibIndex.PlcfHdrtxbxTxt), _doc.HeaderTextBoxStart, _doc.CcpHdrTxbx)
            : _textBoxes ??= TextBoxStories(_doc.Blob(DocFile.FibIndex.PlcftxbxTxt), _doc.TextBoxStart, _doc.CcpTxbx);
        if (!stories.TryGetValue(id, out var story) || !_textBoxesWritten.Add(story.Start))
            return null;
        return story;
    }

    private static Dictionary<int, (int, int)> TextBoxStories(ReadOnlySpan<byte> plc, int offset, int length)
    {
        var stories = new Dictionary<int, (int, int)>();
        var count = Plc.Count(plc.Length, 22);
        for (var i = 0; i < count; i++)
        {
            var (start, end) = (Plc.Cp(plc, i), Plc.Cp(plc, i + 1));
            var lid = BinaryPrimitives.ReadInt32LittleEndian(Plc.Data(plc, count, 22, i)[14..]);
            if (start >= 0 && end > start && end <= length)
                stories.TryAdd(lid, (offset + start, offset + end));
        }
        return stories;
    }
}
