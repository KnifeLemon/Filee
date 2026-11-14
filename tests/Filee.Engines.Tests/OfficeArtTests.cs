// The OfficeArt layer DOC and PPT share (Office/OfficeArt): records and their bounds, property tables with complex
// data, shapes, FBSEs and BLIPs, and the limits on inflating metafiles. Records are written in code.

using System.Buffers.Binary;
using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Office.OfficeArt;

namespace Filee.Engines.Tests;

public class OfficeArtTests
{
    /// <summary>A record: header (recVer, recInstance, recType, recLen) and body.</summary>
    internal static byte[] Record(int type, byte[] body, int instance = 0, int version = 0)
    {
        var record = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)((instance << 4) | version));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)body.Length);
        body.CopyTo(record, 8);
        return record;
    }

    internal static byte[] Container(int type, params byte[][] children) => Record(type, [.. children.SelectMany(c => c)], version: 0xF);

    /// <summary>A property table: (opid, value) entries, then the complex data.</summary>
    internal static byte[] Fopt(int type, (int Opid, uint Value)[] entries, byte[]? complex = null)
    {
        var body = new byte[entries.Length * 6];
        for (var i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(i * 6), (ushort)entries[i].Opid);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(i * 6 + 2), entries[i].Value);
        }
        return Record(type, [.. body, .. complex ?? []], instance: entries.Length, version: 3);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
            zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>A metafile BLIP (one UID): header with size, bounds, size in EMU, saved size and compression, then the data.</summary>
    private static byte[] MetafileBlip(int type, byte[] metafile, bool compress, (int Left, int Top, int Right, int Bottom) bounds = default)
    {
        var data = compress ? Deflate(metafile) : metafile;
        var header = new byte[34];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)metafile.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), bounds.Left);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), bounds.Top);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), bounds.Right);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), bounds.Bottom);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), 914400); // one inch wide
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), 914400);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)data.Length);
        header[32] = compress ? (byte)0 : (byte)0xFE;
        return Record(type, [.. new byte[16], .. header, .. data], instance: type == OfficeArtRecords.BlipEmf ? 0x3D4 : 0x216);
    }

    /// <summary>An FBSE (36 bytes, no name), with its BLIP after it or none and <paramref name="delay"/> as foDelay.</summary>
    private static byte[] Fbse(byte[]? blip, uint delay = 0)
    {
        var body = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), delay);
        return Record(OfficeArtRecords.Fbse, [.. body, .. blip ?? []], instance: 6, version: 2);
    }

    private static OfficeArtRecord At(byte[] bytes, int at = 0) => OfficeArtRecords.At(bytes, at, bytes.Length) ?? throw new InvalidDataException("No record.");

    // ───────────────────────── Records ─────────────────────────

    [Fact]
    public void A_record_is_read_only_when_it_fits_in_what_holds_it()
    {
        var atom = Record(OfficeArtRecords.Fsp, new byte[8], instance: 202, version: 2);
        var container = Container(OfficeArtRecords.SpContainer, atom, Record(OfficeArtRecords.ClientTextbox, new byte[4]));
        var record = At(container);
        Assert.Equal((0xF, 0, OfficeArtRecords.SpContainer, 8, container.Length - 8), (record.Version, record.Instance, record.Type, record.Start, record.Length));
        Assert.True(record.IsContainer);
        Assert.Equal(0, record.HeaderStart);

        var children = OfficeArtRecords.Children(container, record);
        Assert.Equal([OfficeArtRecords.Fsp, OfficeArtRecords.ClientTextbox], children.Select(c => c.Type));
        Assert.Equal((2, 202), (children[0].Version, children[0].Instance));
        Assert.Equal(OfficeArtRecords.ClientTextbox, OfficeArtRecords.Child(container, record, OfficeArtRecords.ClientTextbox)?.Type);
        Assert.Null(OfficeArtRecords.Child(container, record, OfficeArtRecords.Fopt));

        // A length past the end of what holds the record, a header cut short, an end past the bytes.
        Assert.Null(OfficeArtRecords.At(container, 8, 8 + atom.Length - 1));
        Assert.Null(OfficeArtRecords.At(container, container.Length - 4, container.Length));
        Assert.Null(OfficeArtRecords.At(container, 0, container.Length + 1));
        Assert.Null(OfficeArtRecords.At(container, -1, container.Length));
    }

    [Fact]
    public void The_children_of_a_container_end_at_the_first_record_that_does_not_fit()
    {
        var damaged = Record(OfficeArtRecords.Fopt, new byte[12]);
        BinaryPrimitives.WriteUInt32LittleEndian(damaged.AsSpan(4), uint.MaxValue);
        var container = Container(OfficeArtRecords.SpgrContainer, Record(OfficeArtRecords.Fspgr, new byte[16]), damaged, Record(OfficeArtRecords.Fsp, new byte[8]));
        Assert.Equal([OfficeArtRecords.Fspgr], OfficeArtRecords.Children(container, At(container)).Select(c => c.Type));
    }

    // ───────────────────────── Properties ─────────────────────────

    [Fact]
    public void A_property_table_gives_simple_values_and_where_complex_data_is()
    {
        var vertices = new byte[] { 1, 2, 3, 4, 5, 6 };
        var segments = new byte[] { 7, 8 };
        var fopt = Fopt(OfficeArtRecords.Fopt,
        [
            (OfficeArtProperties.FillColor, 0x00332211),
            (0x8000 | OfficeArtProperties.Vertices, (uint)vertices.Length),
            (OfficeArtProperties.FillBooleans, (1u << 20) | (1u << 4)),
            (0x4000 | OfficeArtProperties.Pib, 3),
            (0x8000 | OfficeArtProperties.SegmentInfo, (uint)segments.Length),
        ], [.. vertices, .. segments]);
        var properties = new Dictionary<int, uint>();
        var complex = new Dictionary<int, (int Start, int Length)>();
        OfficeArtProperties.Read(fopt, At(fopt), properties, complex: complex);

        Assert.Equal([OfficeArtProperties.Pib, OfficeArtProperties.FillColor, OfficeArtProperties.FillBooleans], properties.Keys.Order());
        Assert.Equal(3u, properties[OfficeArtProperties.Pib]);
        Assert.Equal("112233", OfficeArtProperties.Rgb(properties, OfficeArtProperties.FillColor));
        Assert.True(OfficeArtProperties.Flag(properties, OfficeArtProperties.FillBooleans, 4, fallback: false));
        Assert.True(OfficeArtProperties.Flag(properties, OfficeArtProperties.LineBooleans, 3, fallback: true));
        Assert.Equal(vertices, fopt.AsSpan(complex[OfficeArtProperties.Vertices].Start, complex[OfficeArtProperties.Vertices].Length).ToArray());
        Assert.Equal(segments, fopt.AsSpan(complex[OfficeArtProperties.SegmentInfo].Start, complex[OfficeArtProperties.SegmentInfo].Length).ToArray());

        // A scheme colour (high byte set) isn't a plain RGB colour.
        properties[OfficeArtProperties.LineColor] = 0x08000001;
        Assert.Null(OfficeArtProperties.Rgb(properties, OfficeArtProperties.LineColor));
    }

    [Fact]
    public void Complex_data_and_entries_that_do_not_fit_are_left_out()
    {
        // Five entries announced, two present; the second claims more complex data than the record holds.
        var fopt = Fopt(OfficeArtRecords.Fopt, [(OfficeArtProperties.LineWidth, 12700), (0x8000 | OfficeArtProperties.Vertices, 1000)], [1, 2, 3]);
        BinaryPrimitives.WriteUInt16LittleEndian(fopt, (5 << 4) | 3);
        var properties = new Dictionary<int, uint>();
        var complex = new Dictionary<int, (int Start, int Length)>();
        OfficeArtProperties.Read(fopt, At(fopt), properties, complex: complex);
        Assert.Equal(12700u, Assert.Single(properties).Value);
        Assert.Empty(complex);
    }

    [Fact]
    public void A_shape_has_its_fsp_and_the_primary_values_over_the_tertiary_ones()
    {
        var fsp = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(fsp, 1025);
        BinaryPrimitives.WriteUInt32LittleEndian(fsp.AsSpan(4), OfficeArtShape.FlipH | 0xA00);
        var container = Container(OfficeArtRecords.SpContainer,
            Record(OfficeArtRecords.Fsp, fsp, instance: 202, version: 2),
            Fopt(OfficeArtRecords.Fopt, [(OfficeArtProperties.FillColor, 0x0000FF), (OfficeArtProperties.TextLeft, 0)]),
            Fopt(OfficeArtRecords.TertiaryFopt, [(OfficeArtProperties.FillColor, 0x00FF00), (OfficeArtProperties.GroupBooleans, 0x200020)]),
            Fopt(OfficeArtRecords.SecondaryFopt, [(OfficeArtProperties.TextLeft, 91440)]),
            Record(OfficeArtRecords.ClientAnchor, new byte[8]));
        var shape = OfficeArtShape.Read(container, At(container));
        Assert.Equal((1025, 202), (shape.Id, shape.Type));
        Assert.NotEqual(0u, shape.Flags & OfficeArtShape.FlipH);
        Assert.Equal(0x0000FFu, shape.Properties[OfficeArtProperties.FillColor]);
        Assert.Equal(0x200020u, shape.Properties[OfficeArtProperties.GroupBooleans]);
        Assert.Equal(91440u, shape.Properties[OfficeArtProperties.TextLeft]);
        Assert.Equal(5, shape.Records.Count);
        Assert.Equal(OfficeArtRecords.ClientAnchor, shape.Records[^1].Type);
    }

    // ───────────────────────── BLIPs ─────────────────────────

    private static readonly DocumentLimits Limits = DocumentLimits.Default;

    [Fact]
    public void Png_jpeg_and_dib_blips_give_their_pictures()
    {
        var png = DocumentFuzzTests.TinyPng(3, 2);
        var blip = Record(OfficeArtRecords.BlipPng, [.. new byte[16], 0xFF, .. png], instance: 0x6E0);
        var picture = OfficeArtBlips.Read(blip, At(blip), Limits, _ => { });
        Assert.Equal("png", picture?.Extension);
        Assert.Equal(png, picture?.Bytes);

        // An odd instance has a second UID.
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var twoUids = Record(OfficeArtRecords.BlipJpeg, [.. new byte[32], 0xFF, .. jpeg], instance: 0x46B);
        Assert.Equal(jpeg, OfficeArtBlips.Read(twoUids, At(twoUids), Limits, _ => { })?.Bytes);

        // A DIB (40-byte header, 8 bits, two palette colours) gets a BMP file header pointing past header and palette.
        var dib = new byte[40 + 8 + 4];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 8);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(32), 2);
        var record = Record(OfficeArtRecords.BlipDib, [.. new byte[16], 0xFF, .. dib], instance: 0x7A8);
        var bmp = OfficeArtBlips.Read(record, At(record), Limits, _ => { });
        Assert.Equal("bmp", bmp?.Extension);
        Assert.Equal((byte)'B', bmp!.Value.Bytes[0]);
        Assert.Equal(14 + 40 + 8, BinaryPrimitives.ReadInt32LittleEndian(bmp.Value.Bytes.AsSpan(10)));

        // PICT isn't read.
        var pict = Record(OfficeArtRecords.BlipPict, new byte[64], instance: 0x542);
        Assert.Null(OfficeArtBlips.Read(pict, At(pict), Limits, _ => { }));
    }

    [Fact]
    public void A_deflated_emf_is_inflated_and_counted()
    {
        var emf = Enumerable.Range(0, 5000).Select(i => (byte)(i % 7)).ToArray();
        var record = MetafileBlip(OfficeArtRecords.BlipEmf, emf, compress: true);
        long counted = 0;
        var picture = OfficeArtBlips.Read(record, At(record), Limits, n => counted += n);
        Assert.Equal("emf", picture?.Extension);
        Assert.Equal(emf, picture?.Bytes);
        Assert.Equal(emf.Length, counted);

        // Damaged deflate data gives no picture.
        var damaged = MetafileBlip(OfficeArtRecords.BlipEmf, emf, compress: true);
        damaged.AsSpan(8 + 16 + 34, 8).Fill(0xFF);
        Assert.Null(OfficeArtBlips.Read(damaged, At(damaged), Limits, _ => { }));
    }

    [Fact]
    public void A_wmf_gets_its_placeable_header()
    {
        var wmf = new byte[] { 1, 0, 9, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var record = MetafileBlip(OfficeArtRecords.BlipWmf, wmf, compress: false, (0, 0, 2000, 1000));
        var picture = OfficeArtBlips.Read(record, At(record), Limits, _ => { });
        Assert.Equal("wmf", picture?.Extension);
        var bytes = picture!.Value.Bytes;
        Assert.Equal(0x9AC6CDD7u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(2000, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(10)));
        Assert.Equal(2000, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14))); // 2000 units per inch
        ushort checksum = 0;
        for (var i = 0; i < 20; i += 2)
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i));
        Assert.Equal(checksum, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(wmf, bytes[22..]);
    }

    [Fact]
    public void A_metafile_that_inflates_past_the_ratio_is_refused()
    {
        var record = MetafileBlip(OfficeArtRecords.BlipEmf, new byte[2_000_000], compress: true);
        var strict = DocumentLimits.Default with { InflateRatioFloor = 64 * 1024, MaxInflateRatio = 50 };
        var refused = Assert.Throws<ConversionRefusedException>(() => OfficeArtBlips.Read(record, At(record), strict, _ => { }));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);

        // The part limit stops inflating at its size.
        var small = DocumentLimits.Default with { MaxPartBytes = 1000 };
        Assert.Equal(1000, OfficeArtBlips.Read(record, At(record), small, _ => { })?.Bytes.Length);
    }

    [Fact]
    public void An_fbse_gives_its_own_blip_or_the_one_at_its_delay_offset()
    {
        var png = DocumentFuzzTests.TinyPng(2, 2);
        var blip = Record(OfficeArtRecords.BlipPng, [.. new byte[16], 0xFF, .. png], instance: 0x6E0);

        var embedded = Fbse(blip);
        var found = OfficeArtBlips.FbseBlip(embedded, At(embedded), [], out var delayed);
        Assert.False(delayed);
        Assert.Equal(OfficeArtRecords.BlipPng, found?.Type);
        Assert.Equal(8 + 36 + 8, found?.Start);

        byte[] stream = [.. new byte[10], .. blip];
        var stored = Fbse(null, delay: 10);
        found = OfficeArtBlips.FbseBlip(stored, At(stored), stream, out delayed);
        Assert.True(delayed);
        Assert.Equal(png, OfficeArtBlips.Read(stream, found!.Value, Limits, _ => { })?.Bytes);

        // A delay past the stream, or a record that isn't an FBSE.
        var lost = Fbse(null, delay: 1_000_000);
        Assert.Null(OfficeArtBlips.FbseBlip(lost, At(lost), stream, out _));
        Assert.Null(OfficeArtBlips.FbseBlip(blip, At(blip), stream, out _));
    }
}
