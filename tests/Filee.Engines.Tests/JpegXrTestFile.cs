// A minimal JPEG XR file written bit by bit (ITU-T T.832 Annex A and clause 8), for tests that run where Windows' own
// encoder doesn't: a 16 × 16 grey picture of one macroblock, DC band only, with a DC of zero (mid grey).

namespace Filee.Engines.Tests;

internal static class JpegXrTestFile
{
    /// <summary>The file; every sample decodes to 128.</summary>
    public static byte[] DcOnlyGrey()
    {
        var bits = new List<int>();
        void Put(long value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                bits.Add((int)((value >> i) & 1));
        }
        void Align()
        {
            while (bits.Count % 8 != 0)
                bits.Add(0);
        }

        // IMAGE_HEADER (Table 20).
        foreach (var c in "WMPHOTO\0")
            Put(c, 8);
        Put(1, 4);  // RESERVED_B
        Put(0, 1);  // HARD_TILING_FLAG
        Put(1, 3);  // RESERVED_C
        Put(0, 1);  // TILING_FLAG
        Put(0, 1);  // FREQUENCY_MODE_CODESTREAM_FLAG
        Put(0, 3);  // SPATIAL_XFRM_SUBORDINATE
        Put(0, 1);  // INDEX_TABLE_PRESENT_FLAG
        Put(0, 2);  // OVERLAP_MODE
        Put(1, 1);  // SHORT_HEADER_FLAG
        Put(1, 1);  // LONG_WORD_FLAG
        Put(0, 1);  // WINDOWING_FLAG
        Put(0, 1);  // TRIM_FLEXBITS_FLAG
        Put(0, 1);  // RESERVED_D
        Put(0, 1);  // RED_BLUE_NOT_SWAPPED_FLAG
        Put(0, 1);  // PREMULTIPLIED_ALPHA_FLAG
        Put(0, 1);  // ALPHA_IMAGE_PLANE_FLAG
        Put(0, 4);  // OUTPUT_CLR_FMT: YONLY
        Put(1, 4);  // OUTPUT_BITDEPTH: BD8
        Put(15, 16); // WIDTH_MINUS1
        Put(15, 16); // HEIGHT_MINUS1
        // IMAGE_PLANE_HEADER (Table 27): YONLY, not scaled, DC only, one uniform DC QP.
        Put(0, 3);
        Put(0, 1);
        Put(3, 4);
        Put(1, 1);
        Put(1, 8);
        Align();
        Put(0, 16); // SubsequentBytes = VLW_ESC( ) = 0
        // TILE_SPATIAL (Table 39): start code, arbitrary byte, one macroblock: IS_DC_CH_FLAG 0, DC_REF of 8 bits.
        Put(1, 24);
        Put(0, 8);
        Put(0, 1);
        Put(0, 8);
        Align();
        var codestream = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
            codestream[i / 8] |= (byte)(bits[i] << (7 - i % 8));

        // The tag-based file (Annex A): header, one directory, the 8bppGray pixel format GUID, the codestream.
        const int Entries = 5;
        var ifdLength = 2 + Entries * 12 + 4;
        var guidAt = 8 + ifdLength;
        var dataAt = guidAt + 16;
        using var file = new MemoryStream();
        using var w = new BinaryWriter(file);
        w.Write((byte)0x49);
        w.Write((byte)0x49);
        w.Write((byte)0xBC);
        w.Write((byte)1);
        w.Write(8u);
        w.Write((ushort)Entries);
        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            w.Write(tag);
            w.Write(type);
            w.Write(count);
            w.Write(value);
        }
        Entry(0xBC01, 1, 16, (uint)guidAt);
        Entry(0xBC80, 4, 1, 16);
        Entry(0xBC81, 4, 1, 16);
        Entry(0xBCC0, 4, 1, (uint)dataAt);
        Entry(0xBCC1, 4, 1, (uint)codestream.Length);
        w.Write(0u);
        w.Write(new Guid("6fddc324-4e03-4bfe-b185-3d77768dc908").ToByteArray());
        w.Write(codestream);
        return file.ToArray();
    }
}
