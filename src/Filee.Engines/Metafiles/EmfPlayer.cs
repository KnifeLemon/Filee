// Reads an Enhanced Metafile ([MS-EMF]) record by record and draws it through GdiPlayer. Written from Microsoft's
// [MS-EMF] specification; record layouts are given there in section 2.3.
//
// EMF+ ([MS-EMFPLUS]) records travel inside EMR_COMMENT records. An "EMF+ dual" file (what GDI+ writes by default)
// holds the whole drawing twice, as EMF+ and as plain EMF records: the EMF records are drawn and the EMF+ ones skipped.
// An "EMF+ only" file has no EMF drawing to fall back on and is refused with NotSupportedException, so a caller can
// use another renderer.
//
// Not drawn: OpenGL records (EMR_GLSRECORD), flood fills (they need the pixels already drawn), colour management and
// right-to-left layout (EMR_SETLAYOUT). Everything else is drawn or is state the drawing doesn't need.

using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace Filee.Engines.Metafiles;

/// <summary>The header of an EMF ([MS-EMF] 2.2.9 Header).</summary>
/// <param name="Bounds">The drawing's bounds in device units, right and bottom included.</param>
/// <param name="Frame">The picture frame in 0.01 mm, right and bottom included.</param>
/// <param name="Handles">Entries of the object table, the reserved entry 0 included.</param>
/// <param name="Device">The reference device's size in pixels.</param>
/// <param name="Millimeters">The reference device's size in millimetres.</param>
internal sealed record EmfHeader(SKRectI Bounds, SKRectI Frame, uint Bytes, uint Records, int Handles, SKSizeI Device, SKSizeI Millimeters)
{
    /// <summary>Device units per millimetre of the reference device (96 dpi when the header doesn't say).</summary>
    public (double X, double Y) UnitsPerMm =>
        (Device.Width > 0 && Millimeters.Width > 0 ? (double)Device.Width / Millimeters.Width : 96 / 25.4,
         Device.Height > 0 && Millimeters.Height > 0 ? (double)Device.Height / Millimeters.Height : 96 / 25.4);

    /// <summary>The picture in device units: the frame, or the bounds when the frame is empty.</summary>
    public SKRect Picture
    {
        get
        {
            var (x, y) = UnitsPerMm;
            if (Frame.Right > Frame.Left && Frame.Bottom > Frame.Top)
                return new SKRect((float)(Frame.Left * x / 100), (float)(Frame.Top * y / 100), (float)(Frame.Right * x / 100), (float)(Frame.Bottom * y / 100));
            if (Bounds.Right >= Bounds.Left && Bounds.Bottom >= Bounds.Top)
                return new SKRect(Bounds.Left, Bounds.Top, Bounds.Right + 1, Bounds.Bottom + 1);
            return SKRect.Empty;
        }
    }

    /// <summary>Points per device unit.</summary>
    public float PointsPerUnit => (float)(72 / 25.4 / UnitsPerMm.X);

    public const uint Signature = 0x464D4520; // " EMF"

    /// <summary>Reads the header record at the start of <paramref name="data"/>.</summary>
    public static EmfHeader Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 88 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(data[40..]) != Signature)
            throw new InvalidDataException("The file is not an EMF metafile (no EMF header).");
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (size < 88 || size > data.Length)
            throw new InvalidDataException("The EMF file is damaged: its header is cut off.");
        static SKRectI Rect(ReadOnlySpan<byte> d) => new(BinaryPrimitives.ReadInt32LittleEndian(d), BinaryPrimitives.ReadInt32LittleEndian(d[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(d[8..]), BinaryPrimitives.ReadInt32LittleEndian(d[12..]));
        static SKSizeI Size(ReadOnlySpan<byte> d) => new(BinaryPrimitives.ReadInt32LittleEndian(d), BinaryPrimitives.ReadInt32LittleEndian(d[4..]));
        return new EmfHeader(Rect(data[8..]), Rect(data[24..]), BinaryPrimitives.ReadUInt32LittleEndian(data[48..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[52..]), BinaryPrimitives.ReadUInt16LittleEndian(data[56..]), Size(data[72..]), Size(data[80..]));
    }
}

/// <summary>Plays EMF records.</summary>
internal sealed class EmfPlayer
{
    private const uint EmfPlusIdentifier = 0x2B464D45; // "EMF+"

    private readonly GdiPlayer _gdi;
    private readonly MetafileLimits _limits;
    private readonly GdiObject?[] _objects;
    private bool _sawEmfPlus;

    private EmfPlayer(GdiPlayer gdi, EmfHeader header, MetafileLimits limits)
    {
        _gdi = gdi;
        _limits = limits;
        if (header.Handles > limits.MaxObjects)
            throw MetafileLimits.Exceeded($"The metafile's object table has {header.Handles:N0} entries, more than {limits.MaxObjects:N0}.");
        _objects = new GdiObject?[Math.Max(1, header.Handles)];
    }

    /// <summary>Draws the records of <paramref name="data"/> (a whole EMF file).</summary>
    public static void Play(ReadOnlySpan<byte> data, EmfHeader header, GdiPlayer gdi, MetafileLimits limits, CancellationToken cancellationToken)
    {
        var player = new EmfPlayer(gdi, header, limits);
        // The header's byte count ends the records when it is smaller than the file (data after it is not the metafile's).
        var end = header.Bytes >= 88 && header.Bytes < data.Length ? (int)header.Bytes : data.Length;
        var offset = 0;
        var index = 0;
        while (offset < end)
        {
            if (end - offset < 8)
                throw new InvalidDataException($"The EMF file is damaged: it ends {end - offset} bytes into a record (record {index}, at byte {offset:N0}).");
            var type = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            var record = new MetafileRecord(data.Slice(offset, (int)Math.Min(size, (uint)(end - offset))), "EMF", (int)Math.Min(type, int.MaxValue), index, offset);
            if (size < 8)
                throw record.Damaged($"says it is {size} bytes long, shorter than a record can be");
            if (size > end - offset)
                throw record.Damaged($"says it is {size:N0} bytes long, but the file ends {end - offset:N0} bytes after its start");
            if (++index > limits.MaxRecords)
                throw MetafileLimits.Exceeded($"The metafile has more than {limits.MaxRecords:N0} records.");
            if ((index & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (type == 14) // EMR_EOF
                return;
            if (index > 1)
                player.Record(record);
            offset += (int)size;
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
                case 2: // EMR_POLYBEZIER
                    gdi.PolyBezier(Points(r, 24, wide: true), fromCurrent: false);
                    break;
                case 85:
                    gdi.PolyBezier(Points(r, 24, wide: false), fromCurrent: false);
                    break;
                case 3: // EMR_POLYGON
                    gdi.Polygon(Points(r, 24, wide: true));
                    break;
                case 86:
                    gdi.Polygon(Points(r, 24, wide: false));
                    break;
                case 4: // EMR_POLYLINE
                    gdi.Polyline(Points(r, 24, wide: true), fromCurrent: false);
                    break;
                case 87:
                    gdi.Polyline(Points(r, 24, wide: false), fromCurrent: false);
                    break;
                case 5: // EMR_POLYBEZIERTO
                    gdi.PolyBezier(Points(r, 24, wide: true), fromCurrent: true);
                    break;
                case 88:
                    gdi.PolyBezier(Points(r, 24, wide: false), fromCurrent: true);
                    break;
                case 6: // EMR_POLYLINETO
                    gdi.Polyline(Points(r, 24, wide: true), fromCurrent: true);
                    break;
                case 89:
                    gdi.Polyline(Points(r, 24, wide: false), fromCurrent: true);
                    break;
                case 7: // EMR_POLYPOLYLINE
                    gdi.PolyPolyline(PolyPoints(r, wide: true));
                    break;
                case 90:
                    gdi.PolyPolyline(PolyPoints(r, wide: false));
                    break;
                case 8: // EMR_POLYPOLYGON
                    gdi.PolyPolygon(PolyPoints(r, wide: true));
                    break;
                case 91:
                    gdi.PolyPolygon(PolyPoints(r, wide: false));
                    break;
                case 56: // EMR_POLYDRAW
                case 92:
                    {
                        var wide = r.Type == 56;
                        var points = Points(r, 24, wide);
                        gdi.PolyDraw(points, r.Bytes(28 + points.Length * (wide ? 8 : 4), points.Length));
                        break;
                    }
                case 9: // EMR_SETWINDOWEXTEX
                    gdi.SetWindowExtent(r.I32(8), r.I32(12));
                    break;
                case 10: // EMR_SETWINDOWORGEX
                    gdi.SetWindowOrigin(r.I32(8), r.I32(12));
                    break;
                case 11: // EMR_SETVIEWPORTEXTEX
                    gdi.SetViewportExtent(r.I32(8), r.I32(12));
                    break;
                case 12: // EMR_SETVIEWPORTORGEX
                    gdi.SetViewportOrigin(r.I32(8), r.I32(12));
                    break;
                case 13: // EMR_SETBRUSHORGEX
                    dc.BrushOrigin = new SKPoint(r.I32(8), r.I32(12));
                    break;
                case 15: // EMR_SETPIXELV
                    gdi.SetPixel(r.I32(8), r.I32(12), gdi.Color(r.U32(16)));
                    break;
                case 17: // EMR_SETMAPMODE
                    gdi.SetMapMode(r.I32(8));
                    break;
                case 18: // EMR_SETBKMODE
                    dc.OpaqueBackground = r.U32(8) == 2;
                    break;
                case 19: // EMR_SETPOLYFILLMODE
                    dc.Winding = r.U32(8) == 2;
                    break;
                case 20: // EMR_SETROP2
                    dc.Rop2 = r.I32(8);
                    break;
                case 21: // EMR_SETSTRETCHBLTMODE
                    dc.StretchMode = r.I32(8);
                    break;
                case 22: // EMR_SETTEXTALIGN
                    dc.TextAlign = r.I32(8);
                    break;
                case 24: // EMR_SETTEXTCOLOR
                    dc.TextColor = gdi.Color(r.U32(8));
                    break;
                case 25: // EMR_SETBKCOLOR
                    dc.BackgroundColor = gdi.Color(r.U32(8));
                    break;
                case 26: // EMR_OFFSETCLIPRGN
                    gdi.OffsetClip(r.I32(8), r.I32(12));
                    break;
                case 27: // EMR_MOVETOEX
                    gdi.MoveTo(r.I32(8), r.I32(12));
                    break;
                case 28: // EMR_SETMETARGN
                    gdi.SetMetaRegion();
                    break;
                case 29: // EMR_EXCLUDECLIPRECT
                    gdi.ClipRectangle(Rect(r, 8), exclude: true);
                    break;
                case 30: // EMR_INTERSECTCLIPRECT
                    gdi.ClipRectangle(Rect(r, 8), exclude: false);
                    break;
                case 31: // EMR_SCALEVIEWPORTEXTEX
                    gdi.ScaleViewportExtent(r.I32(8), r.I32(12), r.I32(16), r.I32(20));
                    break;
                case 32: // EMR_SCALEWINDOWEXTEX
                    gdi.ScaleWindowExtent(r.I32(8), r.I32(12), r.I32(16), r.I32(20));
                    break;
                case 33: // EMR_SAVEDC
                    gdi.SaveDc();
                    break;
                case 34: // EMR_RESTOREDC
                    gdi.RestoreDc(r.I32(8));
                    break;
                case 35: // EMR_SETWORLDTRANSFORM
                    gdi.SetWorldTransform(Transform(r, 8));
                    break;
                case 36: // EMR_MODIFYWORLDTRANSFORM
                    gdi.ModifyWorldTransform(Transform(r, 8), r.I32(32));
                    break;
                case 37: // EMR_SELECTOBJECT
                    if (Selected(r, r.U32(8)) is { } selected)
                        gdi.Select(selected);
                    break;
                case 38: // EMR_CREATEPEN
                    Store(r, r.U32(8), new GdiPen(r.I32(12), r.I32(16), gdi.Color(r.U32(24))));
                    break;
                case 39: // EMR_CREATEBRUSHINDIRECT
                    Store(r, r.U32(8), Brush(r.U32(12), gdi.Color(r.U32(16)), r.I32(20)));
                    break;
                case 40: // EMR_DELETEOBJECT
                    {
                        var handle = r.U32(8);
                        if (handle < _objects.Length)
                            _objects[handle] = null;
                        break;
                    }
                case 41: // EMR_ANGLEARC
                    gdi.AngleArc(new SKPoint(r.I32(8), r.I32(12)), r.U32(16), r.F32(20), r.F32(24));
                    break;
                case 42: // EMR_ELLIPSE
                    gdi.Ellipse(Rect(r, 8));
                    break;
                case 43: // EMR_RECTANGLE
                    gdi.Rectangle(Rect(r, 8));
                    break;
                case 44: // EMR_ROUNDRECT
                    gdi.RoundRectangle(Rect(r, 8), r.I32(24), r.I32(28));
                    break;
                case 45: // EMR_ARC
                case 46: // EMR_CHORD
                case 47: // EMR_PIE
                case 55: // EMR_ARCTO
                    gdi.Arc(r.Type switch { 45 => 0, 46 => 1, 47 => 2, _ => 3 }, Rect(r, 8),
                        new SKPoint(r.I32(24), r.I32(28)), new SKPoint(r.I32(32), r.I32(36)));
                    break;
                case 48: // EMR_SELECTPALETTE
                    if (Selected(r, r.U32(8)) is GdiPalette selectedPalette)
                        gdi.Select(selectedPalette);
                    break;
                case 49: // EMR_CREATEPALETTE
                    {
                        var count = r.Count(r.U16(14), 4, 16);
                        var entries = new SKColor[count];
                        for (var i = 0; i < count; i++)
                            entries[i] = new SKColor(r.U8(16 + i * 4), r.U8(17 + i * 4), r.U8(18 + i * 4));
                        Store(r, r.U32(8), new GdiPalette(entries));
                        break;
                    }
                case 50: // EMR_SETPALETTEENTRIES
                    {
                        if (Selected(r, r.U32(8)) is not GdiPalette palette)
                            break;
                        var start = r.U32(12);
                        var count = r.Count(r.U32(16), 4, 20);
                        for (var i = 0; i < count && start + i < palette.Entries.Length; i++)
                            palette.Entries[start + i] = new SKColor(r.U8(20 + i * 4), r.U8(21 + i * 4), r.U8(22 + i * 4));
                        break;
                    }
                case 54: // EMR_LINETO
                    gdi.LineTo(r.I32(8), r.I32(12));
                    break;
                case 57: // EMR_SETARCDIRECTION
                    dc.Clockwise = r.U32(8) == 2;
                    break;
                case 58: // EMR_SETMITERLIMIT (an integer by the specification; some writers store a float)
                    {
                        var raw = r.U32(8);
                        var limit = raw is > 0 and < 1000 ? raw : BitConverter.UInt32BitsToSingle(raw);
                        if (float.IsFinite(limit) && limit >= 1)
                            dc.MiterLimit = limit;
                        break;
                    }
                case 59: // EMR_BEGINPATH
                    gdi.BeginPath();
                    break;
                case 60: // EMR_ENDPATH
                    gdi.EndPath();
                    break;
                case 61: // EMR_CLOSEFIGURE
                    gdi.CloseFigure();
                    break;
                case 62: // EMR_FILLPATH
                    gdi.FillPath();
                    break;
                case 63: // EMR_STROKEANDFILLPATH
                    gdi.StrokeAndFillPath();
                    break;
                case 64: // EMR_STROKEPATH
                    gdi.StrokePath();
                    break;
                case 65: // EMR_FLATTENPATH: curves stay curves, which draws the same
                    break;
                case 66: // EMR_WIDENPATH
                    gdi.WidenPath();
                    break;
                case 67: // EMR_SELECTCLIPPATH
                    gdi.SelectClipPath(r.I32(8));
                    break;
                case 68: // EMR_ABORTPATH
                    gdi.AbortPath();
                    break;
                case 70: // EMR_COMMENT
                    Comment(r);
                    break;
                case 71: // EMR_FILLRGN
                    if (Selected(r, r.U32(28)) is GdiBrush fillBrush)
                    {
                        using var region = Region(r, 32, r.U32(24));
                        gdi.FillRegion(region, fillBrush);
                    }
                    break;
                case 72: // EMR_FRAMERGN
                    if (Selected(r, r.U32(28)) is GdiBrush frameBrush)
                    {
                        using var region = Region(r, 40, r.U32(24));
                        gdi.FrameRegion(region, frameBrush, r.I32(32), r.I32(36));
                    }
                    break;
                case 73: // EMR_INVERTRGN
                    using (var region = Region(r, 28, r.U32(24)))
                        gdi.InvertRegion(region);
                    break;
                case 74: // EMR_PAINTRGN
                    using (var region = Region(r, 28, r.U32(24)))
                        gdi.PaintRegion(region);
                    break;
                case 75: // EMR_EXTSELECTCLIPRGN
                    {
                        var size = r.U32(8);
                        var mode = r.I32(12);
                        if (size == 0)
                        {
                            if (mode == GdiPlayer.RgnCopy)
                                gdi.SelectClip(null, GdiPlayer.RgnCopy);
                            break;
                        }
                        using var region = Region(r, 16, size);
                        gdi.SelectClip(region, mode);
                        break;
                    }
                case 76: // EMR_BITBLT
                case 77: // EMR_STRETCHBLT
                case 114: // EMR_ALPHABLEND
                case 116: // EMR_TRANSPARENTBLT
                    Blit(r);
                    break;
                case 78: // EMR_MASKBLT
                    MaskBlit(r);
                    break;
                case 79: // EMR_PLGBLT
                    {
                        using var image = Bitmap(r, 92, 96, DibOptions.Default);
                        if (image is not null)
                        {
                            gdi.Parallelogram(image, new SKRect(r.I32(48), r.I32(52), r.I32(48) + r.I32(56), r.I32(52) + r.I32(60)),
                                new SKPoint(r.I32(24), r.I32(28)), new SKPoint(r.I32(32), r.I32(36)), new SKPoint(r.I32(40), r.I32(44)));
                        }
                        break;
                    }
                case 80: // EMR_SETDIBITSTODEVICE
                    SetDibitsToDevice(r);
                    break;
                case 81: // EMR_STRETCHDIBITS
                    StretchDibits(r);
                    break;
                case 82: // EMR_EXTCREATEFONTINDIRECTW
                    Store(r, r.U32(8), Font(r));
                    break;
                case 83: // EMR_EXTTEXTOUTA
                case 84: // EMR_EXTTEXTOUTW
                    Text(r, 36, r.I32(24) == 2, wide: r.Type == 84);
                    break;
                case 96: // EMR_POLYTEXTOUTA
                case 97: // EMR_POLYTEXTOUTW
                    {
                        var count = r.Count(r.U32(36), 40, 40);
                        for (var i = 0; i < count; i++)
                            Text(r, 40 + i * 40, r.I32(24) == 2, wide: r.Type == 97);
                        break;
                    }
                case 108: // EMR_SMALLTEXTOUT
                    SmallText(r);
                    break;
                case 93: // EMR_CREATEMONOBRUSH
                case 94: // EMR_CREATEDIBPATTERNBRUSHPT
                    {
                        // The brush keeps the image (objects live as long as the drawing).
                        var image = Bitmap(r, 16, 24, Options(r.U32(12)));
                        Store(r, r.U32(8), image is null
                            ? new GdiBrush(GdiBrush.Solid, new SKColor(0x80, 0x80, 0x80))
                            : new GdiBrush(GdiBrush.DibPatternPt, SKColors.Black, 0, image.Image, monochrome: r.Type == 93));
                        break;
                    }
                case 95: // EMR_EXTCREATEPEN
                    Store(r, r.U32(8), ExtendedPen(r));
                    break;
                case 99: // EMR_CREATECOLORSPACE
                case 122: // EMR_CREATECOLORSPACEW
                    Store(r, r.U32(8), GdiPlaceholder.Instance);
                    break;
                case 118: // EMR_GRADIENTFILL
                    Gradient(r);
                    break;
                default:
                    // State the drawing doesn't need (mapper flags, colour adjustment and management, palettes realized,
                    // layout, text justification, escapes), flood fills and OpenGL.
                    break;
            }
        }
        catch (DibFormatException ex)
        {
            throw r.Damaged(ex.Message);
        }
    }

    // ───────────────────────── Objects ─────────────────────────

    private void Store(MetafileRecord r, uint handle, GdiObject item)
    {
        if (handle == 0 || handle >= _objects.Length)
            throw r.Damaged($"creates object {handle}, outside its object table of {_objects.Length} entries");
        _objects[handle] = item;
    }

    /// <summary>The object a record names: a stock object (high bit set) or an entry of the table (null if empty).</summary>
    private GdiObject? Selected(MetafileRecord r, uint handle)
    {
        if ((handle & 0x80000000) != 0)
            return Stock(handle & 0x7FFFFFFF);
        if (handle >= _objects.Length)
            throw r.Damaged($"uses object {handle}, outside its object table of {_objects.Length} entries");
        return _objects[handle];
    }

    private static readonly GdiFont FixedFont = new(-13, 0, 0, 400, false, false, false, GdiFont.AnsiCharSet, "Courier New");
    private static readonly GdiFont VariableFont = new(-13, 0, 0, 400, false, false, false, GdiFont.AnsiCharSet, "Arial");
    private static readonly GdiFont GuiFont = new(-11, 0, 0, 400, false, false, false, GdiFont.DefaultCharSet, "Segoe UI");

    private static GdiObject? Stock(uint index) => index switch
    {
        0 => GdiBrush.White,
        1 => GdiBrush.LightGray,
        2 => GdiBrush.Gray,
        3 => GdiBrush.DarkGray,
        4 => GdiBrush.Black,
        5 => GdiBrush.Empty,
        6 => GdiPen.White,
        7 => GdiPen.Black,
        8 => GdiPen.Empty,
        10 or 11 or 16 => FixedFont,
        12 => VariableFont,
        13 or 14 => GdiFont.Default,
        17 => GuiFont,
        18 => GdiBrush.White, // DC_BRUSH
        19 => GdiPen.Black, // DC_PEN
        _ => null,
    };

    private static GdiBrush Brush(uint style, SKColor color, int hatch) => style switch
    {
        GdiBrush.Null => GdiBrush.Empty,
        GdiBrush.Hatched => new GdiBrush(GdiBrush.Hatched, color, hatch),
        _ => new GdiBrush(GdiBrush.Solid, color),
    };

    private GdiPen ExtendedPen(MetafileRecord r)
    {
        var style = r.I32(28);
        var width = r.U32(32);
        var brushStyle = r.U32(36);
        var color = _gdi.Color(r.U32(40));
        var entries = r.Count(r.U32(48), 4, 52);
        float[]? dashes = null;
        if ((style & GdiPen.StyleMask) == GdiPen.UserStyle && entries > 0)
        {
            dashes = new float[Math.Min(entries, 64)];
            for (var i = 0; i < dashes.Length; i++)
                dashes[i] = r.U32(52 + i * 4);
        }
        if (brushStyle == GdiBrush.Null)
            style = (style & ~GdiPen.StyleMask) | GdiPen.Null;
        return new GdiPen(style, width, color, dashes, extended: true);
    }

    private static GdiFont Font(MetafileRecord r)
    {
        var face = new StringBuilder();
        for (var at = 40; at + 1 < Math.Min(r.Length, 104); at += 2)
        {
            var c = (char)r.U16(at);
            if (c == '\0')
                break;
            face.Append(c);
        }
        return new GdiFont(r.I32(12), r.I32(16), r.I32(20), r.I32(28), r.U8(32) != 0, r.U8(33) != 0, r.U8(34) != 0, r.U8(35), face.ToString());
    }

    // ───────────────────────── Geometry ─────────────────────────

    private static SKRect Rect(MetafileRecord r, int at) => new(r.I32(at), r.I32(at + 4), r.I32(at + 8), r.I32(at + 12));

    private static SKMatrix Transform(MetafileRecord r, int at) =>
        new(r.F32(at), r.F32(at + 8), r.F32(at + 16), r.F32(at + 4), r.F32(at + 12), r.F32(at + 20), 0, 0, 1);

    /// <summary>The points of a poly record: a count at <paramref name="at"/>, then 32-bit or 16-bit x / y pairs.</summary>
    private static SKPoint[] Points(MetafileRecord r, int at, bool wide)
    {
        var size = wide ? 8 : 4;
        var count = r.Count(r.U32(at), size, at + 4);
        return Read(r, at + 4, count, wide);
    }

    private static SKPoint[] Read(MetafileRecord r, int at, int count, bool wide)
    {
        var points = new SKPoint[count];
        for (var i = 0; i < count; i++)
        {
            points[i] = wide
                ? new SKPoint(r.I32(at + i * 8), r.I32(at + i * 8 + 4))
                : new SKPoint(r.I16(at + i * 4), r.I16(at + i * 4 + 2));
        }
        return points;
    }

    /// <summary>The figures of a polypoly record: number of figures, number of points, each figure's count, the points.</summary>
    private static List<SKPoint[]> PolyPoints(MetafileRecord r, bool wide)
    {
        var size = wide ? 8 : 4;
        var figures = r.Count(r.U32(24), 4, 32);
        var total = r.Count(r.U32(28), size, 32 + figures * 4);
        var points = Read(r, 32 + figures * 4, total, wide);
        var result = new List<SKPoint[]>(figures);
        var start = 0;
        for (var i = 0; i < figures; i++)
        {
            var count = r.U32(32 + i * 4);
            if (count > total - start)
                throw r.Damaged($"has figures of more points ({start + (long)count:N0}) than it holds ({total:N0})");
            result.Add(points[start..(start + (int)count)]);
            start += (int)count;
        }
        return result;
    }

    /// <summary>A RegionData object ([MS-EMF] 2.2.24): a header and rectangles in device units.</summary>
    private SKPath Region(MetafileRecord r, int at, uint size)
    {
        var data = r.Bytes(at, size);
        if (data.Length < 32)
            throw r.Damaged("has a region without a header");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (count > _limits.MaxRegionRectangles)
            throw MetafileLimits.Exceeded($"A region in the metafile has more than {_limits.MaxRegionRectangles:N0} rectangles.");
        if (32 + count * 16L > data.Length)
            throw r.Damaged($"has a region of {count:N0} rectangles, more than its {data.Length:N0} bytes hold");
        var path = new SKPath();
        for (var i = 0; i < count; i++)
        {
            var rect = data.Slice(32 + i * 16, 16);
            var box = new SKRect(BinaryPrimitives.ReadInt32LittleEndian(rect), BinaryPrimitives.ReadInt32LittleEndian(rect[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(rect[8..]), BinaryPrimitives.ReadInt32LittleEndian(rect[12..])).Standardized;
            if (!box.IsEmpty)
                path.AddRect(box);
        }
        return path;
    }

    // ───────────────────────── Bitmaps ─────────────────────────

    /// <summary>The bitmap of a record from its offset / size fields at <paramref name="bmi"/> and <paramref name="bits"/>.</summary>
    private DibImage? Bitmap(MetafileRecord r, int bmi, int bits, DibOptions options)
    {
        var bmiSize = r.U32(bmi + 4);
        if (bmiSize == 0)
            return null;
        return Dib.Decode(r.At(r.U32(bmi), bmiSize), r.At(r.U32(bits), r.U32(bits + 4)), options, _gdi.Budget);
    }

    private DibOptions Options(uint usage, bool alpha = false, SKColor? transparent = null) =>
        new(PaletteIndices: usage == 1, Palette: _gdi.Dc.Palette?.Entries, Alpha: alpha, TransparentColor: transparent);

    /// <summary>EMR_BITBLT, EMR_STRETCHBLT, EMR_ALPHABLEND and EMR_TRANSPARENTBLT, which share their layout.</summary>
    private void Blit(MetafileRecord r)
    {
        float x = r.I32(24), y = r.I32(28), width = r.I32(32), height = r.I32(36);
        var stretch = r.Type != 76;
        var rop = r.Type is 114 or 116 ? 0x00CC0020u : r.U32(40);
        byte alpha = 255;
        var options = Options(r.U32(80));
        if (r.Type == 114)
        {
            alpha = r.U8(42);
            options = Options(r.U32(80), alpha: (r.U8(43) & 1) != 0);
        }
        else if (r.Type == 116)
        {
            options = Options(r.U32(80), transparent: _gdi.Color(r.U32(40)));
        }
        using var image = Bitmap(r, 84, 92, options);
        if (image is null)
        {
            _gdi.PatternBlit(new SKRect(x, y, x + width, y + height), rop);
            return;
        }
        float sourceX = r.I32(44), sourceY = r.I32(48);
        var sourceWidth = stretch ? r.I32(100) : width;
        var sourceHeight = stretch ? r.I32(104) : height;
        _gdi.Bitmap(image, new SKRect(sourceX, sourceY, sourceX + sourceWidth, sourceY + sourceHeight), x, y, width, height, rop, alpha);
    }

    /// <summary>EMR_MASKBLT: where the monochrome mask is 1 the source is drawn; where it is 0 the background operation applies.</summary>
    private void MaskBlit(MetafileRecord r)
    {
        float x = r.I32(24), y = r.I32(28), width = r.I32(32), height = r.I32(36);
        var rop4 = r.U32(40);
        using var image = Bitmap(r, 84, 92, Options(r.U32(80)));
        if (image is null)
        {
            _gdi.PatternBlit(new SKRect(x, y, x + width, y + height), rop4 & 0x00FFFFFF);
            return;
        }
        float sourceX = r.I32(44), sourceY = r.I32(48);
        var source = new SKRect(sourceX, sourceY, sourceX + width, sourceY + height);
        using var mask = Bitmap(r, 112, 120, DibOptions.Default);
        if (mask is null || ((rop4 >> 24) & 0xFF) != 0xAA)
        {
            _gdi.Bitmap(image, source, x, y, width, height, rop4 & 0x00FFFFFF);
            return;
        }
        // Background operation "leave the destination": the mask cuts the source out.
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.DrawImage(image.Image, 0, 0);
        using (var cut = new SKPaint { BlendMode = SKBlendMode.DstIn, ColorFilter = SKColorFilter.CreateLumaColor() })
            surface.Canvas.DrawImage(mask.Image, sourceX - r.I32(100), sourceY - r.I32(104), cut);
        using var masked = new DibImage(surface.Snapshot(), false);
        _gdi.Bitmap(masked, source, x, y, width, height, 0x00CC0020);
    }

    private void StretchDibits(MetafileRecord r)
    {
        float x = r.I32(24), y = r.I32(28);
        float sourceX = r.I32(32), sourceY = r.I32(36), sourceWidth = r.I32(40), sourceHeight = r.I32(44);
        var rop = r.U32(68);
        float width = r.I32(72), height = r.I32(76);
        using var image = Bitmap(r, 48, 56, Options(r.U32(64)));
        if (image is null)
        {
            _gdi.PatternBlit(new SKRect(x, y, x + width, y + height), rop);
            return;
        }
        // For a bottom-up DIB the source rectangle is measured from the bottom-left corner.
        var top = BottomUp(r, 48) ? image.Height - sourceY - sourceHeight : sourceY;
        _gdi.Bitmap(image, new SKRect(sourceX, top, sourceX + sourceWidth, top + sourceHeight), x, y, width, height, rop);
    }

    private void SetDibitsToDevice(MetafileRecord r)
    {
        float x = r.I32(24), y = r.I32(28);
        float sourceX = r.I32(32), sourceY = r.I32(36), width = r.I32(40), height = r.I32(44);
        var bmiSize = r.U32(52);
        if (bmiSize < 12)
            return;
        var info = r.At(r.U32(48), bmiSize).ToArray();
        var bits = r.At(r.U32(56), r.U32(60));
        var startScan = r.U32(68);
        var scans = r.U32(72);
        // The record may carry only some scan lines: the bitmap is read as that many rows.
        var bottomUp = true;
        if (info.Length >= 40 && BinaryPrimitives.ReadInt32LittleEndian(info) >= 40)
        {
            var rows = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(8));
            bottomUp = rows > 0;
            if (scans > 0 && scans < Math.Abs((long)rows))
                BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(8), bottomUp ? (int)scans : -(int)scans);
            else
                startScan = 0;
        }
        else
        {
            startScan = 0;
        }
        using var image = Dib.Decode(info, bits, Options(r.U32(64)), _gdi.Budget);
        if (image is null)
            return;
        var rowY = sourceY - startScan;
        var top = bottomUp ? image.Height - rowY - height : rowY;
        _gdi.Bitmap(image, new SKRect(sourceX, top, sourceX + width, top + height), x, y, width, height, 0x00CC0020);
    }

    /// <summary>True when the DIB the offset field at <paramref name="bmi"/> points to is stored bottom-up.</summary>
    private static bool BottomUp(MetafileRecord r, int bmi)
    {
        var info = r.At(r.U32(bmi), r.U32(bmi + 4));
        if (info.Length < 12)
            return true;
        return BinaryPrimitives.ReadInt32LittleEndian(info) == 12 || BinaryPrimitives.ReadInt32LittleEndian(info[8..]) > 0;
    }

    private void Gradient(MetafileRecord r)
    {
        var vertices = r.Count(r.U32(24), 16, 36);
        var mode = r.U32(32);
        var pieceSize = mode == 2 ? 12 : 8;
        var pieces = r.Count(r.U32(28), pieceSize, 36 + vertices * 16);
        var points = new SKPoint[vertices];
        var colors = new SKColor[vertices];
        for (var i = 0; i < vertices; i++)
        {
            var at = 36 + i * 16;
            points[i] = new SKPoint(r.I32(at), r.I32(at + 4));
            colors[i] = new SKColor((byte)(r.U16(at + 8) >> 8), (byte)(r.U16(at + 10) >> 8), (byte)(r.U16(at + 12) >> 8));
        }
        var start = 36 + vertices * 16;
        for (var i = 0; i < pieces; i++)
        {
            var at = start + i * pieceSize;
            if (mode == 2)
            {
                int a = Vertex(r, at, vertices), b = Vertex(r, at + 4, vertices), c = Vertex(r, at + 8, vertices);
                _gdi.GradientTriangle(points[a], colors[a], points[b], colors[b], points[c], colors[c]);
            }
            else
            {
                int a = Vertex(r, at, vertices), b = Vertex(r, at + 4, vertices);
                _gdi.GradientRectangle(points[a], colors[a], points[b], colors[b], vertical: mode == 1);
            }
        }
    }

    private static int Vertex(MetafileRecord r, int at, int vertices)
    {
        var index = r.U32(at);
        if (index >= vertices)
            throw r.Damaged($"names vertex {index} of {vertices}");
        return (int)index;
    }

    // ───────────────────────── Text ─────────────────────────

    /// <summary>An EmrText object ([MS-EMF] 2.2.5) at <paramref name="at"/>, with the string and advances it points to.</summary>
    private void Text(MetafileRecord r, int at, bool advanced, bool wide)
    {
        float x = r.I32(at), y = r.I32(at + 4);
        var chars = r.U32(at + 8);
        var stringOffset = r.U32(at + 12);
        var options = r.I32(at + 16);
        const int NoRect = 0x0100;
        SKRect? box = (options & NoRect) == 0 ? Rect(r, at + 20) : null;
        var dxOffset = r.U32(at + ((options & NoRect) == 0 ? 36 : 20));
        if (chars > r.Length)
            throw r.Damaged($"has a text of {chars:N0} characters, more than it holds");
        var bytes = r.At(stringOffset, chars * (wide ? 2u : 1u));
        string text;
        int[]? perChar = null;
        if (wide)
        {
            text = Encoding.Unicode.GetString(bytes);
        }
        else
        {
            (text, perChar) = MetafileText.DecodeAnsi(bytes, _gdi.Dc.Font);
        }
        float[]? dx = null, dy = null;
        if (dxOffset != 0 && chars > 0)
        {
            var pairs = (options & GdiPlayer.EtoPdy) != 0;
            var values = r.At(dxOffset, chars * (pairs ? 8u : 4u));
            var perByte = new float[chars];
            var perByteY = pairs ? new float[chars] : null;
            for (var i = 0; i < chars; i++)
            {
                perByte[i] = BinaryPrimitives.ReadInt32LittleEndian(values[(i * (pairs ? 8 : 4))..]);
                if (perByteY is not null)
                    perByteY[i] = -BinaryPrimitives.ReadInt32LittleEndian(values[(i * 8 + 4)..]);
            }
            dx = Combine(perByte, perChar, text.Length);
            dy = perByteY is null ? null : Combine(perByteY, perChar, text.Length);
        }
        _gdi.Text(x, y, text, dx, dy, options, box, advanced);
    }

    /// <summary>Advances given per byte (ANSI text) added up per character; per character (Unicode) as they are.</summary>
    internal static float[] Combine(float[] values, int[]? bytesPerChar, int characters)
    {
        if (bytesPerChar is null)
            return values.Length >= characters ? values : [.. values, .. new float[characters - values.Length]];
        var result = new float[characters];
        var at = 0;
        for (var i = 0; i < characters; i++)
        {
            for (var b = 0; b < bytesPerChar[i] && at < values.Length; b++)
                result[i] += values[at++];
        }
        return result;
    }

    private void SmallText(MetafileRecord r)
    {
        const int NoRect = 0x0100, SmallChars = 0x0200;
        float x = r.I32(8), y = r.I32(12);
        var chars = r.U32(16);
        var options = r.I32(20);
        var advanced = r.I32(24) == 2;
        SKRect? box = (options & NoRect) == 0 ? Rect(r, 36) : null;
        var at = (options & NoRect) == 0 ? 52 : 36;
        if (chars > r.Length)
            throw r.Damaged($"has a text of {chars:N0} characters, more than it holds");
        var bytes = r.Bytes(at, chars * ((options & SmallChars) != 0 ? 1 : 2));
        var text = (options & SmallChars) != 0 ? Encoding.Latin1.GetString(bytes) : Encoding.Unicode.GetString(bytes);
        _gdi.Text(x, y, text, null, null, options & ~SmallChars, box, advanced);
    }

    // ───────────────────────── Comments ─────────────────────────

    /// <summary>
    /// EMR_COMMENT: EMF+ records are skipped, but the first EMF+ header says whether the file is "EMF+ dual" (the EMF
    /// records hold the whole drawing) or "EMF+ only" (they don't).
    /// </summary>
    private void Comment(MetafileRecord r)
    {
        if (_sawEmfPlus || r.Length < 16 || r.U32(12) != EmfPlusIdentifier)
            return;
        _sawEmfPlus = true;
        // EmfPlusHeader: Type 0x4001, Flags (bit 0: dual), Size, DataSize, Version, EmfPlusFlags, LogicalDpiX, LogicalDpiY.
        if (r.Length >= 22 && r.U16(16) == 0x4001 && (r.U16(18) & 1) == 0)
            throw new NotSupportedException("This EMF+ file holds its drawing only as EMF+ records (no EMF fallback), which Filee doesn't draw yet.");
    }
}
