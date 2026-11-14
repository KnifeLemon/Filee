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
// Records, shapes, property tables and BLIPs are read by Office/OfficeArt ([MS-ODRAW]), shared with the PPT reader:
// every length is checked against what holds it; JPEG, PNG and TIFF are copied, DIBs get a BMP file header, EMF and
// WMF are inflated when compressed and a WMF gets its placeable header. Each picture is unpacked and written to
// word/media once, however many characters show it, and what metafiles inflate to counts against the document's
// limits (its total and the inflate ratio).

using System.Buffers.Binary;
using System.Xml.Linq;
using Filee.Engines.Office.OfficeArt;

namespace Filee.Engines.Hwp.Hwpx.Doc;

internal sealed partial class DocToDocx
{
    private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private const int EmuPerTwip = 635;

    /// <summary>A shape of the OfficeArt drawings (type, flags, properties) and the id of its text box story.</summary>
    private sealed class Shape(OfficeArtShape art)
    {
        public OfficeArtShape Art { get; } = art;
        public int Type => Art.Type;
        public uint Flags => Art.Flags;
        public Dictionary<int, uint> Properties => Art.Properties;
        public int? TextId;
    }

    private readonly Dictionary<int, Shape> _shapes = [];

    /// <summary>The BStore: where each BLIP is (an FBSE record in the table stream).</summary>
    private readonly List<(int Offset, int Length)> _bstore = [];

    private Dictionary<int, (int Start, int End)>? _textBoxes, _headerTextBoxes;

    /// <summary>Text boxes being written (a text box can't hold its own story again).</summary>
    private int _inTextBox;

    private readonly HashSet<int> _textBoxesWritten = [];

    /// <summary>
    /// Pictures written to word/media (null: none could be read), by where they are: a BLIP record in a stream ('D'
    /// Data, 'T' table, 'W' WordDocument) or an older picture's PICF ('P') in the Data stream.
    /// </summary>
    private readonly Dictionary<(char Stream, int Offset), string?> _media = [];

    /// <summary>Bytes the picture being read inflated to, counted already (see <see cref="AddMedia"/>).</summary>
    private long _inflated;

    // ───────────────────────── OfficeArt drawings ─────────────────────────

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
        if (OfficeArtRecords.At(table, offset, end) is not { Type: OfficeArtRecords.DggContainer } group)
            return;
        foreach (var child in OfficeArtRecords.Children(table, group).Where(c => c.Type == OfficeArtRecords.BStoreContainer))
        {
            foreach (var fbse in OfficeArtRecords.Children(table, child))
                _bstore.Add((fbse.HeaderStart, fbse.Length + OfficeArtRecords.HeaderSize));
        }
        var at = group.End;
        var stack = new Stack<OfficeArtRecord>();
        while (at + 1 < end && OfficeArtRecords.At(table, at + 1, end) is { Type: OfficeArtRecords.DgContainer } drawing)
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
            foreach (var child in OfficeArtRecords.Children(table, record))
            {
                if (child.Type is OfficeArtRecords.DgContainer or OfficeArtRecords.SpgrContainer)
                    stack.Push(child);
                else if (child.Type == OfficeArtRecords.SpContainer)
                    ReadShape(table, child);
            }
        }
    }

    /// <summary>A shape of the drawings, with the id of its text box (its ClientTextbox).</summary>
    private void ReadShape(byte[] table, OfficeArtRecord container)
    {
        var shape = new Shape(OfficeArtShape.Read(table, container));
        foreach (var child in shape.Art.Records)
        {
            if (child.Type == OfficeArtRecords.ClientTextbox && child.Length >= 4)
                shape.TextId = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(child.Start));
        }
        if (shape.Art.Id is { } spid)
            _shapes.TryAdd(spid, shape);
    }

    // ───────────────────────── BLIPs ─────────────────────────

    /// <summary>
    /// The picture in word/media of an FBSE record at <paramref name="fbse"/> of <paramref name="bytes"/> (the stream
    /// <paramref name="stream"/>): its own BLIP, or the one at foDelay in the WordDocument stream.
    /// </summary>
    private string? FbseMedia(char stream, ReadOnlySpan<byte> bytes, OfficeArtRecord fbse)
    {
        if (OfficeArtBlips.FbseBlip(bytes, fbse, _doc.Word, out var delayed) is not { } blip)
            return null;
        return delayed ? BlipMedia('W', _doc.Word, blip) : BlipMedia(stream, bytes, blip);
    }

    /// <summary>The picture in word/media of a BLIP record, unpacked and written the first time it is shown.</summary>
    private string? BlipMedia(char stream, ReadOnlySpan<byte> bytes, OfficeArtRecord blip)
    {
        if (_media.TryGetValue((stream, blip.Start), out var name))
            return name;
        _inflated = 0;
        name = OfficeArtBlips.Read(bytes, blip, _doc.Limits, CountInflated) is { } picture ? AddMedia(picture.Bytes, picture.Extension) : null;
        _inflated = 0; // a metafile that couldn't be unpacked writes nothing
        _media[(stream, blip.Start)] = name;
        return name;
    }

    /// <summary>Counts what a metafile inflates to against the document, and remembers it for <see cref="AddMedia"/>.</summary>
    private void CountInflated(long bytes)
    {
        _doc.Count(bytes);
        _inflated += bytes;
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

        string? media = null;
        Dictionary<int, uint> properties = [];
        if (mm is 0x64 or 0x66)
        {
            if (mm == 0x66 && start < end)
                start += 1 + data[start];
            if (OfficeArtRecords.At(data, start, end) is { Type: OfficeArtRecords.SpContainer } container)
            {
                foreach (var child in OfficeArtRecords.Children(data, container).Where(c => c.Type is OfficeArtRecords.Fopt or OfficeArtRecords.TertiaryFopt))
                    OfficeArtProperties.Read(data, child, properties, overwrite: child.Type == OfficeArtRecords.Fopt);
                // The FBSEs of the pictures follow the shape container; pib (property 260) picks one, 1-based.
                var fbses = new List<OfficeArtRecord>();
                var next = container.End;
                while (OfficeArtRecords.At(data, next, end) is { } fbse)
                {
                    fbses.Add(fbse);
                    next = fbse.End;
                }
                var pib = (int)properties.GetValueOrDefault(OfficeArtProperties.Pib, 1u);
                if (pib >= 1 && pib <= fbses.Count)
                    media = FbseMedia('D', data, fbses[pib - 1]);
                else if (fbses.Count > 0)
                    media = FbseMedia('D', data, fbses[0]);
            }
        }
        else if (end > start && !_media.TryGetValue(('P', at), out media))
        {
            // An older picture: a metafile (without its placeable header) right after the PICF.
            var bounds = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(bounds.AsSpan(8), Math.Max(1, (int)BinaryPrimitives.ReadInt16LittleEndian(picf[8..])));
            BinaryPrimitives.WriteInt32LittleEndian(bounds.AsSpan(12), Math.Max(1, (int)BinaryPrimitives.ReadInt16LittleEndian(picf[10..])));
            _media[('P', at)] = media = AddMedia(OfficeArtBlips.Placeable(data[start..end], bounds, (dxaGoal * EmuPerTwip, dyaGoal * EmuPerTwip)), "wmf");
        }
        if (media is null)
            return null;

        // The picture as placed: its goal size less the crops, scaled by mx and my (per mille).
        var width = (long)Math.Max(1, dxaGoal - cropLeft - cropRight) * (mx == 0 ? 1000 : mx) / 1000;
        var height = (long)Math.Max(1, dyaGoal - cropTop - cropBottom) * (my == 0 ? 1000 : my) / 1000;
        var id = MediaRelationship(media);
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
        var (top, bottom, left, right) = (Crop(OfficeArtProperties.CropFromTop), Crop(OfficeArtProperties.CropFromBottom), Crop(OfficeArtProperties.CropFromLeft), Crop(OfficeArtProperties.CropFromRight));
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
        if (shape.Properties.TryGetValue(OfficeArtProperties.Pib, out var pib) && pib >= 1 && pib <= _bstore.Count)
        {
            var (offset, length) = _bstore[(int)pib - 1];
            if (OfficeArtRecords.At(_doc.Table, offset, offset + length) is { } fbse && FbseMedia('T', _doc.Table, fbse) is { } media)
                graphic = PictureGraphic(MediaRelationship(media), CropOf(shape.Properties));
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
        if (shape.Properties.TryGetValue(OfficeArtProperties.PosRelH, out var relativeH))
            horizontalFrom = relativeH switch { 0 => "margin", 1 => "page", 3 => "character", _ => "column" };
        if (shape.Properties.TryGetValue(OfficeArtProperties.PosRelV, out var relativeV))
            verticalFrom = relativeV switch { 0 => "margin", 1 => "page", 3 => "line", _ => "paragraph" };
        if (shape.Properties.TryGetValue(OfficeArtProperties.PosH, out var posh))
            horizontalAlign = posh switch { 1 => "left", 2 => "center", 3 => "right", 4 => "inside", 5 => "outside", _ => null };
        if (shape.Properties.TryGetValue(OfficeArtProperties.PosV, out var posv))
            verticalAlign = posv switch { 1 => "top", 2 => "center", 3 => "bottom", 4 => "inside", 5 => "outside", _ => null };
        if (shape.Properties.TryGetValue(OfficeArtProperties.GroupBooleans, out var group) && (group & (1u << 21)) != 0)
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
        var spPr = new XElement(Wps + "spPr",
            new XElement(A + "xfrm",
                (shape.Flags & OfficeArtShape.FlipH) != 0 ? new XAttribute("flipH", 1) : null, (shape.Flags & OfficeArtShape.FlipV) != 0 ? new XAttribute("flipV", 1) : null,
                new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                new XElement(A + "ext", new XAttribute("cx", width), new XAttribute("cy", height))),
            new XElement(A + "prstGeom", new XAttribute("prst", geometry ?? "rect")));
        if (geometry != "line")
        {
            spPr.Add(OfficeArtProperties.Flag(properties, OfficeArtProperties.FillBooleans, 4, true)
                ? new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", OfficeArtProperties.Rgb(properties, OfficeArtProperties.FillColor) ?? "FFFFFF")))
                : new XElement(A + "noFill"));
        }
        spPr.Add(OfficeArtProperties.Flag(properties, OfficeArtProperties.LineBooleans, 3, true)
            ? new XElement(A + "ln", new XAttribute("w", properties.GetValueOrDefault(OfficeArtProperties.LineWidth, 9525u)),
                new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", OfficeArtProperties.Rgb(properties, OfficeArtProperties.LineColor) ?? "000000"))))
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
            new XAttribute("lIns", properties.GetValueOrDefault(OfficeArtProperties.TextLeft, 91440u)), new XAttribute("tIns", properties.GetValueOrDefault(OfficeArtProperties.TextTop, 45720u)),
            new XAttribute("rIns", properties.GetValueOrDefault(OfficeArtProperties.TextRight, 91440u)), new XAttribute("bIns", properties.GetValueOrDefault(OfficeArtProperties.TextBottom, 45720u))));
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
