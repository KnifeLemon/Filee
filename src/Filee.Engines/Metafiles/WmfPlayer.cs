// Reads a Windows Metafile ([MS-WMF]) record by record and draws it through GdiPlayer. Written from Microsoft's
// [MS-WMF] specification; record layouts are given there in section 2.3. Most records store their parameters in
// reverse order (bottom, right, top, left; y before x).
//
// A WMF has no device units of its own. With the placeable (Aldus) header its bounding box, in logical units at
// "Inch" units per inch, is the picture: playback starts as GDI+ starts it, in MM_ANISOTROPIC with the window and the
// viewport both set to that box, and the file's own window records map their window onto it. A WMF without the
// placeable header takes its box from its first SetWindowOrg / SetWindowExt records.
//
// Objects take the lowest free entry of the object table when they are created, and records refer to them by that
// entry. Not drawn: flood fills, escapes (printer commands; an EMF carried in META_ESCAPE comments is not needed, the
// WMF records draw the same picture).

using System.Buffers.Binary;
using SkiaSharp;

namespace Filee.Engines.Metafiles;

/// <summary>The headers of a WMF: the placeable header's box (when there is one) and the META_HEADER.</summary>
/// <param name="Box">The picture in logical units.</param>
/// <param name="UnitsPerInch">Logical units per inch of <paramref name="Box"/>.</param>
/// <param name="Objects">Entries of the object table.</param>
/// <param name="RecordsStart">Byte offset of the first record after the headers.</param>
internal sealed record WmfHeader(SKRect Box, int UnitsPerInch, int Objects, int RecordsStart)
{
    public const uint PlaceableKey = 0x9AC6CDD7;

    /// <summary>The picture in device units (the box's own units), standardized.</summary>
    public SKRect Picture => Box.Standardized;

    public float PointsPerUnit => 72f / UnitsPerInch;

    public (double X, double Y) UnitsPerMm => (UnitsPerInch / 25.4, UnitsPerInch / 25.4);

    /// <summary>True for the 18-byte META_HEADER at <paramref name="at"/>.</summary>
    public static bool IsMetaHeader(ReadOnlySpan<byte> data, int at) =>
        data.Length >= at + 18
        && BinaryPrimitives.ReadUInt16LittleEndian(data[at..]) is 1 or 2
        && BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 2)..]) == 9
        && BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 4)..]) is 0x0100 or 0x0300;

    public static WmfHeader Read(ReadOnlySpan<byte> data)
    {
        var placeable = data.Length >= 22 && BinaryPrimitives.ReadUInt32LittleEndian(data) == PlaceableKey;
        var at = placeable ? 22 : 0;
        if (!IsMetaHeader(data, at))
            throw new InvalidDataException("The file is not a WMF metafile (no WMF header).");
        var objects = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 10)..]);
        var start = at + 18;
        if (placeable)
        {
            var box = new SKRect(BinaryPrimitives.ReadInt16LittleEndian(data[6..]), BinaryPrimitives.ReadInt16LittleEndian(data[8..]),
                BinaryPrimitives.ReadInt16LittleEndian(data[10..]), BinaryPrimitives.ReadInt16LittleEndian(data[12..]));
            var inch = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
            if (box.Width != 0 && box.Height != 0)
                return new WmfHeader(box, inch is > 0 ? inch : 1440, objects, start);
        }
        var (window, mapMode) = Window(data, start);
        if (window is not { } found || found.Width == 0 || found.Height == 0)
            throw new InvalidDataException("The WMF file has no size: neither a placeable header nor a window extent.");
        var unitsPerInch = mapMode switch
        {
            DcState.MapLoMetric => 254,
            DcState.MapHiMetric => 2540,
            DcState.MapLoEnglish => 100,
            DcState.MapHiEnglish => 1000,
            DcState.MapTwips => 1440,
            // Windows and viewports in Office's files are in twips; small ones are screen pixels.
            _ => Math.Max(Math.Abs(found.Width), Math.Abs(found.Height)) >= 4096 ? 1440 : 96,
        };
        return new WmfHeader(found, unitsPerInch, objects, start);
    }

    /// <summary>The first window origin and extent the records set, and the map mode, looking at the first records only.</summary>
    private static (SKRect? Window, int MapMode) Window(ReadOnlySpan<byte> data, int at)
    {
        float x = 0, y = 0;
        float? width = null, height = null;
        var mapMode = DcState.MapText;
        for (var i = 0; i < 1000 && at + 6 <= data.Length; i++)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            var function = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 4)..]);
            if (size < 3 || size * 2L > data.Length - at || function == 0)
                break;
            if (size >= 5 && function is 0x020B or 0x020C)
            {
                var b = BinaryPrimitives.ReadInt16LittleEndian(data[(at + 6)..]);
                var a = BinaryPrimitives.ReadInt16LittleEndian(data[(at + 8)..]);
                if (function == 0x020B && width is null)
                    (x, y) = (a, b);
                else if (function == 0x020C && width is null)
                    (width, height) = (a, b);
            }
            else if (size >= 4 && function == 0x0103)
            {
                mapMode = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 6)..]);
            }
            at += (int)size * 2;
        }
        return (width is { } w && height is { } h ? new SKRect(x, y, x + w, y + h) : null, mapMode);
    }
}

/// <summary>Plays WMF records.</summary>
internal sealed class WmfPlayer
{
    private readonly GdiPlayer _gdi;
    private readonly MetafileLimits _limits;
    private readonly List<GdiObject?> _objects;

    private WmfPlayer(GdiPlayer gdi, WmfHeader header, MetafileLimits limits)
    {
        _gdi = gdi;
        _limits = limits;
        _objects = new List<GdiObject?>(Math.Min(header.Objects, 4096));
        for (var i = 0; i < header.Objects; i++)
            _objects.Add(null);
    }

    /// <summary>Draws the records of <paramref name="data"/> (a whole WMF file).</summary>
    public static void Play(ReadOnlySpan<byte> data, WmfHeader header, GdiPlayer gdi, MetafileLimits limits, CancellationToken cancellationToken)
    {
        var player = new WmfPlayer(gdi, header, limits);
        var dc = gdi.Dc;
        dc.MapMode = DcState.MapAnisotropic;
        // The window as the file sets it, onto the picture as a program shows it: a window whose extent is negative
        // (y up, as in OLE pictures: SetWindowExt(cx, -cy)) is drawn the other way up, as GDI draws it into a rectangle.
        dc.WindowOrigin = new SKPoint(header.Box.Left, header.Box.Top);
        dc.WindowExtent = (header.Box.Width, header.Box.Height);
        dc.ViewportOrigin = new SKPoint(header.Picture.Left, header.Picture.Top);
        dc.ViewportExtent = (header.Picture.Width, header.Picture.Height);

        var offset = header.RecordsStart;
        var index = 1;
        while (offset < data.Length)
        {
            if (data.Length - offset < 6)
                throw new InvalidDataException($"The WMF file is damaged: it ends {data.Length - offset} bytes into a record (record {index}, at byte {offset:N0}).");
            var words = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            var function = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]);
            var length = Math.Min(words * 2L, data.Length - offset);
            var record = new MetafileRecord(data.Slice(offset, (int)length), "WMF", function, index, offset);
            if (words < 3)
                throw record.Damaged($"says it is {words} words long, shorter than a record can be");
            if (words * 2L > data.Length - offset)
                throw record.Damaged($"says it is {words * 2L:N0} bytes long, but the file ends {data.Length - offset:N0} bytes after its start");
            if (++index > limits.MaxRecords)
                throw MetafileLimits.Exceeded($"The metafile has more than {limits.MaxRecords:N0} records.");
            if ((index & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (function == 0) // META_EOF
                return;
            player.Record(record);
            offset += (int)(words * 2);
        }
    }

    private void Record(MetafileRecord r)
    {
        var gdi = _gdi;
        var dc = gdi.Dc;
        try
        {
            switch (r.Type)
            {
                case 0x001E: // META_SAVEDC
                    gdi.SaveDc();
                    break;
                case 0x0127: // META_RESTOREDC
                    gdi.RestoreDc(r.I16(6));
                    break;
                case 0x0102: // META_SETBKMODE
                    dc.OpaqueBackground = r.U16(6) == 2;
                    break;
                case 0x0103: // META_SETMAPMODE
                    gdi.SetMapMode(r.U16(6));
                    break;
                case 0x0104: // META_SETROP2
                    dc.Rop2 = r.U16(6);
                    break;
                case 0x0106: // META_SETPOLYFILLMODE
                    dc.Winding = r.U16(6) == 2;
                    break;
                case 0x0107: // META_SETSTRETCHBLTMODE
                    dc.StretchMode = r.U16(6);
                    break;
                case 0x0108: // META_SETTEXTCHAREXTRA
                    dc.CharExtra = r.I16(6);
                    break;
                case 0x012E: // META_SETTEXTALIGN
                    dc.TextAlign = r.U16(6);
                    break;
                case 0x0201: // META_SETBKCOLOR
                    dc.BackgroundColor = gdi.Color(r.U32(6));
                    break;
                case 0x0209: // META_SETTEXTCOLOR
                    dc.TextColor = gdi.Color(r.U32(6));
                    break;
                case 0x020B: // META_SETWINDOWORG
                    gdi.SetWindowOrigin(r.I16(8), r.I16(6));
                    break;
                case 0x020C: // META_SETWINDOWEXT
                    gdi.SetWindowExtent(r.I16(8), r.I16(6));
                    break;
                case 0x020D: // META_SETVIEWPORTORG
                    gdi.SetViewportOrigin(r.I16(8), r.I16(6));
                    break;
                case 0x020E: // META_SETVIEWPORTEXT
                    gdi.SetViewportExtent(r.I16(8), r.I16(6));
                    break;
                case 0x020F: // META_OFFSETWINDOWORG
                    gdi.OffsetWindowOrigin(r.I16(8), r.I16(6));
                    break;
                case 0x0211: // META_OFFSETVIEWPORTORG
                    gdi.OffsetViewportOrigin(r.I16(8), r.I16(6));
                    break;
                case 0x0410: // META_SCALEWINDOWEXT
                    gdi.ScaleWindowExtent(r.I16(12), r.I16(10), r.I16(8), r.I16(6));
                    break;
                case 0x0412: // META_SCALEVIEWPORTEXT
                    gdi.ScaleViewportExtent(r.I16(12), r.I16(10), r.I16(8), r.I16(6));
                    break;
                case 0x0213: // META_LINETO
                    gdi.LineTo(r.I16(8), r.I16(6));
                    break;
                case 0x0214: // META_MOVETO
                    gdi.MoveTo(r.I16(8), r.I16(6));
                    break;
                case 0x0220: // META_OFFSETCLIPRGN
                    gdi.OffsetClip(r.I16(8), r.I16(6));
                    break;
                case 0x0415: // META_EXCLUDECLIPRECT
                    gdi.ClipRectangle(Box(r, 6), exclude: true);
                    break;
                case 0x0416: // META_INTERSECTCLIPRECT
                    gdi.ClipRectangle(Box(r, 6), exclude: false);
                    break;
                case 0x0418: // META_ELLIPSE
                    gdi.Ellipse(Box(r, 6));
                    break;
                case 0x041B: // META_RECTANGLE
                    gdi.Rectangle(Box(r, 6));
                    break;
                case 0x061C: // META_ROUNDRECT
                    gdi.RoundRectangle(Box(r, 10), r.I16(8), r.I16(6));
                    break;
                case 0x0817: // META_ARC
                case 0x081A: // META_PIE
                case 0x0830: // META_CHORD
                    gdi.Arc(r.Type switch { 0x0817 => 0, 0x081A => 2, _ => 1 }, Box(r, 14),
                        new SKPoint(r.I16(12), r.I16(10)), new SKPoint(r.I16(8), r.I16(6)));
                    break;
                case 0x0324: // META_POLYGON
                    gdi.Polygon(Points(r, 8, r.Count(r.I16(6), 4, 8)));
                    break;
                case 0x0325: // META_POLYLINE
                    gdi.Polyline(Points(r, 8, r.Count(r.I16(6), 4, 8)), fromCurrent: false);
                    break;
                case 0x0538: // META_POLYPOLYGON
                    gdi.PolyPolygon(PolyPolygon(r));
                    break;
                case 0x041F: // META_SETPIXEL
                    gdi.SetPixel(r.I16(12), r.I16(10), gdi.Color(r.U32(6)));
                    break;
                case 0x061D: // META_PATBLT
                    {
                        float x = r.I16(16), y = r.I16(14), width = r.I16(12), height = r.I16(10);
                        gdi.PatternBlit(new SKRect(x, y, x + width, y + height), r.U32(6));
                        break;
                    }
                case 0x0521: // META_TEXTOUT
                    {
                        var length = r.Count(r.I16(6), 1, 8);
                        var padded = (length + 1) & ~1;
                        var (text, _) = MetafileText.DecodeAnsi(r.Bytes(8, length), dc.Font);
                        gdi.Text(r.I16(10 + padded), r.I16(8 + padded), text, null, null, 0, null, advanced: false);
                        break;
                    }
                case 0x0A32: // META_EXTTEXTOUT
                    ExtTextOut(r);
                    break;
                case 0x02FA: // META_CREATEPENINDIRECT
                    Create(new GdiPen(r.U16(6), Math.Abs(r.I16(8)), gdi.Color(r.U32(12))));
                    break;
                case 0x02FC: // META_CREATEBRUSHINDIRECT
                    {
                        var style = r.U16(6);
                        var color = gdi.Color(r.U32(8));
                        Create(style switch
                        {
                            GdiBrush.Null => GdiBrush.Empty,
                            GdiBrush.Hatched => new GdiBrush(GdiBrush.Hatched, color, r.U16(12)),
                            _ => new GdiBrush(GdiBrush.Solid, color),
                        });
                        break;
                    }
                case 0x02FB: // META_CREATEFONTINDIRECT
                    Create(Font(r));
                    break;
                case 0x00F7: // META_CREATEPALETTE
                    {
                        var count = r.Count(r.U16(8), 4, 10);
                        var entries = new SKColor[count];
                        for (var i = 0; i < count; i++)
                            entries[i] = new SKColor(r.U8(10 + i * 4), r.U8(11 + i * 4), r.U8(12 + i * 4));
                        Create(new GdiPalette(entries));
                        break;
                    }
                case 0x0142: // META_DIBCREATEPATTERNBRUSH
                    {
                        var style = r.U16(6);
                        // The brush keeps the image (objects live as long as the drawing).
                        var image = style == GdiBrush.Pattern
                            ? Bitmap16(r.Bytes(10, r.Length - 10), dc.TextColor, dc.BackgroundColor)
                            : Dib.DecodePacked(r.Bytes(10, r.Length - 10), Options(r.U16(8)), gdi.Budget);
                        Create(image is null
                            ? new GdiBrush(GdiBrush.Solid, new SKColor(0x80, 0x80, 0x80))
                            : new GdiBrush(GdiBrush.DibPatternPt, SKColors.Black, 0, image.Image));
                        break;
                    }
                case 0x01F9: // META_CREATEPATTERNBRUSH: a Bitmap16 header, 18 reserved bytes, the pattern's bits
                    {
                        var image = r.Length >= 38 ? Bitmap16(r.Bytes(6, r.Length - 6), dc.TextColor, dc.BackgroundColor, bitsAt: 32) : null;
                        Create(image is null
                            ? new GdiBrush(GdiBrush.Solid, new SKColor(0x80, 0x80, 0x80))
                            : new GdiBrush(GdiBrush.DibPatternPt, SKColors.Black, 0, image.Image));
                        break;
                    }
                case 0x06FF: // META_CREATEREGION
                    Create(new GdiRegion(Region(r)));
                    break;
                case 0x012D: // META_SELECTOBJECT
                case 0x0234: // META_SELECTPALETTE
                    if (Object(r, r.U16(6)) is { } selected)
                        gdi.Select(selected);
                    break;
                case 0x012C: // META_SELECTCLIPREGION
                    if (Object(r, r.U16(6)) is GdiRegion clip)
                        gdi.Select(clip);
                    else
                        gdi.SelectClip(null, GdiPlayer.RgnCopy);
                    break;
                case 0x01F0: // META_DELETEOBJECT
                    {
                        var handle = r.U16(6);
                        if (handle < _objects.Count)
                            _objects[handle] = null;
                        break;
                    }
                case 0x0228: // META_FILLREGION
                    if (Object(r, r.U16(6)) is GdiRegion fillRegion && Object(r, r.U16(8)) is GdiBrush fillBrush)
                    {
                        using var device = gdi.Device(fillRegion.Path);
                        gdi.FillRegion(device, fillBrush);
                    }
                    break;
                case 0x0429: // META_FRAMEREGION
                    if (Object(r, r.U16(6)) is GdiRegion frameRegion && Object(r, r.U16(8)) is GdiBrush frameBrush)
                    {
                        using var device = gdi.Device(frameRegion.Path);
                        gdi.FrameRegion(device, frameBrush, r.I16(12), r.I16(10));
                    }
                    break;
                case 0x012A: // META_INVERTREGION
                case 0x012B: // META_PAINTREGION
                    if (Object(r, r.U16(6)) is GdiRegion region)
                    {
                        using var device = gdi.Device(region.Path);
                        if (r.Type == 0x012A)
                            gdi.InvertRegion(device);
                        else
                            gdi.PaintRegion(device);
                    }
                    break;
                case 0x0940: // META_DIBBITBLT
                case 0x0922: // META_BITBLT
                    Blit(r, stretch: false, dib: r.Type == 0x0940);
                    break;
                case 0x0B41: // META_DIBSTRETCHBLT
                case 0x0B23: // META_STRETCHBLT
                    Blit(r, stretch: true, dib: r.Type == 0x0B41);
                    break;
                case 0x0F43: // META_STRETCHDIB
                    {
                        float x = r.I16(26), y = r.I16(24), width = r.I16(22), height = r.I16(20);
                        float sourceX = r.I16(18), sourceY = r.I16(16), sourceWidth = r.I16(14), sourceHeight = r.I16(12);
                        var dib = r.Bytes(28, r.Length - 28);
                        using var image = Dib.DecodePacked(dib, Options(r.U16(10)), gdi.Budget);
                        if (image is null)
                            break;
                        var top = BottomUp(dib) ? image.Height - sourceY - sourceHeight : sourceY;
                        gdi.Bitmap(image, new SKRect(sourceX, top, sourceX + sourceWidth, top + sourceHeight), x, y, width, height, r.U32(6));
                        break;
                    }
                case 0x0D33: // META_SETDIBTODEV
                    {
                        float x = r.I16(22), y = r.I16(20), width = r.I16(18), height = r.I16(16);
                        float sourceX = r.I16(14), sourceY = r.I16(12);
                        var dib = r.Bytes(24, r.Length - 24);
                        using var image = Dib.DecodePacked(dib, Options(r.U16(6)), gdi.Budget);
                        if (image is null)
                            break;
                        var top = BottomUp(dib) ? image.Height - sourceY - height : sourceY;
                        gdi.Bitmap(image, new SKRect(sourceX, top, sourceX + width, top + height), x, y, width, height, 0x00CC0020);
                        break;
                    }
                default:
                    // Palettes realized and animated, mapper flags, layout, text justification, escapes, flood fills.
                    break;
            }
        }
        catch (DibFormatException ex)
        {
            throw r.Damaged(ex.Message);
        }
    }

    // ───────────────────────── Objects ─────────────────────────

    /// <summary>Puts a new object into the lowest free entry of the table (growing it when a writer counted too few).</summary>
    private void Create(GdiObject item)
    {
        for (var i = 0; i < _objects.Count; i++)
        {
            if (_objects[i] is null)
            {
                _objects[i] = item;
                return;
            }
        }
        if (_objects.Count >= _limits.MaxObjects)
            throw MetafileLimits.Exceeded($"The metafile creates more than {_limits.MaxObjects:N0} objects without deleting them.");
        _objects.Add(item);
    }

    private GdiObject? Object(MetafileRecord r, int index)
    {
        if (index >= _objects.Count)
            throw r.Damaged($"uses object {index}, outside its object table of {_objects.Count} entries");
        return _objects[index];
    }

    private static GdiFont Font(MetafileRecord r)
    {
        var face = new List<byte>();
        for (var at = 24; at < Math.Min(r.Length, 56); at++)
        {
            var b = r.U8(at);
            if (b == 0)
                break;
            face.Add(b);
        }
        var charSet = r.Length > 19 ? r.U8(19) : GdiFont.DefaultCharSet;
        var (name, _) = MetafileText.DecodeAnsi(face.ToArray(), new GdiFont(0, 0, 0, 400, false, false, false, charSet, ""));
        return new GdiFont(r.I16(6), r.I16(8), r.I16(10), r.I16(14), r.U8(16) != 0, r.U8(17) != 0, r.U8(18) != 0, charSet, name);
    }

    private DibOptions Options(int usage) => new(PaletteIndices: usage == 1, Palette: _gdi.Dc.Palette?.Entries);

    // ───────────────────────── Geometry ─────────────────────────

    /// <summary>A rectangle stored bottom, right, top, left.</summary>
    private static SKRect Box(MetafileRecord r, int at) => new(r.I16(at + 6), r.I16(at + 4), r.I16(at + 2), r.I16(at));

    private static SKPoint[] Points(MetafileRecord r, int at, int count)
    {
        var points = new SKPoint[count];
        for (var i = 0; i < count; i++)
            points[i] = new SKPoint(r.I16(at + i * 4), r.I16(at + i * 4 + 2));
        return points;
    }

    private static List<SKPoint[]> PolyPolygon(MetafileRecord r)
    {
        var polygons = r.Count(r.U16(6), 2, 8);
        var at = 8 + polygons * 2;
        var result = new List<SKPoint[]>(polygons);
        for (var i = 0; i < polygons; i++)
        {
            var count = r.Count(r.U16(8 + i * 2), 4, at);
            result.Add(Points(r, at, count));
            at += count * 4;
        }
        return result;
    }

    /// <summary>A Region object ([MS-WMF] 2.2.1.5): scans of a top, a bottom and left / right pairs, in logical units.</summary>
    private SKPath Region(MetafileRecord r)
    {
        var scans = r.I16(16);
        var at = 28;
        var path = new SKPath();
        var rectangles = 0;
        for (var s = 0; s < scans; s++)
        {
            var count = r.U16(at);
            float top = r.I16(at + 2), bottom = r.I16(at + 4);
            r.Count(count, 2, at + 6);
            for (var i = 0; i + 1 < count; i += 2)
            {
                if (++rectangles > _limits.MaxRegionRectangles)
                    throw MetafileLimits.Exceeded($"A region in the metafile has more than {_limits.MaxRegionRectangles:N0} rectangles.");
                var box = new SKRect(r.I16(at + 6 + i * 2), top, r.I16(at + 8 + i * 2), bottom).Standardized;
                if (!box.IsEmpty)
                    path.AddRect(box);
            }
            at += 6 + count * 2 + 2;
        }
        return path;
    }

    // ───────────────────────── Text ─────────────────────────

    private void ExtTextOut(MetafileRecord r)
    {
        float y = r.I16(6), x = r.I16(8);
        var length = r.I16(10);
        var options = r.U16(12);
        var at = 14;
        SKRect? box = null;
        if ((options & (GdiPlayer.EtoOpaque | GdiPlayer.EtoClipped)) != 0)
        {
            box = new SKRect(r.I16(14), r.I16(16), r.I16(18), r.I16(20));
            at = 22;
        }
        var count = r.Count(length, 1, at);
        var (text, perChar) = MetafileText.DecodeAnsi(r.Bytes(at, count), _gdi.Dc.Font);
        var dxAt = at + ((count + 1) & ~1);
        float[]? dx = null;
        if (count > 0 && r.Length >= dxAt + count * 2)
        {
            var perByte = new float[count];
            for (var i = 0; i < count; i++)
                perByte[i] = r.I16(dxAt + i * 2);
            dx = EmfPlayer.Combine(perByte, perChar, text.Length);
        }
        _gdi.Text(x, y, text, dx, null, options, box, advanced: false);
    }

    // ───────────────────────── Bitmaps ─────────────────────────

    /// <summary>
    /// META_DIBBITBLT, META_DIBSTRETCHBLT (a DIB) and META_BITBLT, META_STRETCHBLT (a Bitmap16). Without a bitmap the
    /// record has one more (reserved) parameter and is a pattern blit.
    /// </summary>
    private void Blit(MetafileRecord r, bool stretch, bool dib)
    {
        var parameters = (r.Type >> 8) & 0xFF;
        var noBitmap = r.Length / 2 == parameters + 3;
        var rop = r.U32(6);
        var at = 10;
        float sourceWidth = 0, sourceHeight = 0;
        if (stretch)
        {
            sourceHeight = r.I16(at);
            sourceWidth = r.I16(at + 2);
            at += 4;
        }
        float sourceY = r.I16(at), sourceX = r.I16(at + 2);
        at += noBitmap ? 6 : 4;
        float height = r.I16(at), width = r.I16(at + 2), y = r.I16(at + 4), x = r.I16(at + 6);
        at += 8;
        if (!stretch)
        {
            sourceWidth = width;
            sourceHeight = height;
        }
        if (noBitmap)
        {
            _gdi.PatternBlit(new SKRect(x, y, x + width, y + height), rop);
            return;
        }
        var bytes = r.Bytes(at, r.Length - at);
        using var image = dib ? Dib.DecodePacked(bytes, Options(0), _gdi.Budget) : Bitmap16(bytes, _gdi.Dc.TextColor, _gdi.Dc.BackgroundColor);
        if (image is null)
            return;
        _gdi.Bitmap(image, new SKRect(sourceX, sourceY, sourceX + sourceWidth, sourceY + sourceHeight), x, y, width, height, rop);
    }

    private static bool BottomUp(ReadOnlySpan<byte> dib) =>
        dib.Length < 12 || BinaryPrimitives.ReadInt32LittleEndian(dib) == 12 || BinaryPrimitives.ReadInt32LittleEndian(dib[8..]) > 0;

    /// <summary>
    /// A Bitmap16 ([MS-WMF] 2.2.2.1): a device-dependent bitmap, top-down. Monochrome ones draw their 0 bits in the
    /// text colour and their 1 bits in the background colour; 24- and 32-bit ones are BGR. Others are left out.
    /// </summary>
    private DibImage? Bitmap16(ReadOnlySpan<byte> data, SKColor zero, SKColor one, int bitsAt = 10)
    {
        if (data.Length < 10)
            throw new DibFormatException("has a bitmap without a header");
        int width = BinaryPrimitives.ReadInt16LittleEndian(data[2..]), height = BinaryPrimitives.ReadInt16LittleEndian(data[4..]);
        int widthBytes = BinaryPrimitives.ReadInt16LittleEndian(data[6..]), planes = data[8], bpp = data[9];
        if (width <= 0 || height <= 0 || widthBytes <= 0 || planes != 1 || bpp is not (1 or 24 or 32))
            return null;
        _gdi.Budget.Bitmap(width, height);
        if (bitsAt + (long)widthBytes * height > data.Length || widthBytes * 8L < (long)width * bpp)
            throw new DibFormatException($"has a bitmap of {width} × {height} pixels whose pixel data is cut off");
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < height; y++)
        {
            var row = data.Slice(bitsAt + y * widthBytes, widthBytes);
            for (var x = 0; x < width; x++)
            {
                var color = bpp == 1
                    ? ((row[x / 8] >> (7 - x % 8)) & 1) == 0 ? zero : one
                    : new SKColor(row[x * bpp / 8 + 2], row[x * bpp / 8 + 1], row[x * bpp / 8]);
                bitmap.SetPixel(x, y, color);
            }
        }
        bitmap.SetImmutable();
        return new DibImage(SKImage.FromBitmap(bitmap), bpp == 1);
    }
}
