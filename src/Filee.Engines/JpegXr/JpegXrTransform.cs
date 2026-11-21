// The integer inverse transform and overlap filter operations of JPEG XR (ITU-T T.832 9.9.7, 9.9.8), exactly as the
// spec writes them: every decoder gets the same samples.

namespace Filee.Engines.JpegXr;

internal static class JpegXrTransform
{
    /// <summary>ICT4x4( ) (Table 160) on 16 coefficients in raster order.</summary>
    public static void Ict4x4(Span<int> c)
    {
        Span<int> temp = stackalloc int[16];
        for (var i = 0; i < 16; i++)
            temp[JpegXrTables.InvPermArr[i]] = c[i];
        temp.CopyTo(c);
        T2x2h(ref c[0], ref c[1], ref c[4], ref c[5], 1);
        InvTodd(ref c[2], ref c[3], ref c[6], ref c[7]);
        InvTodd(ref c[8], ref c[12], ref c[9], ref c[13]);
        InvToddodd(ref c[10], ref c[11], ref c[14], ref c[15]);
        T2x2h(ref c[0], ref c[3], ref c[12], ref c[15], 0);
        T2x2h(ref c[5], ref c[6], ref c[9], ref c[10], 0);
        T2x2h(ref c[1], ref c[2], ref c[13], ref c[14], 0);
        T2x2h(ref c[4], ref c[7], ref c[8], ref c[11], 0);
    }

    /// <summary>T2x2h( ) (Table 161).</summary>
    public static void T2x2h(ref int c0, ref int c1, ref int c2, ref int c3, int round)
    {
        c0 += c3;
        c1 -= c2;
        var t1 = (c0 - c1 + round) >> 1;
        var t2 = c2;
        c2 = t1 - c3;
        c3 = t1 - t2;
        c0 -= c3;
        c1 += c2;
    }

    /// <summary>InvTodd( ) (Table 162).</summary>
    private static void InvTodd(ref int c0, ref int c1, ref int c2, ref int c3)
    {
        c1 += c3;
        c0 -= c2;
        c3 -= c1 >> 1;
        c2 += (c0 + 1) >> 1;
        c0 -= (3 * c1 + 4) >> 3;
        c1 += (3 * c0 + 4) >> 3;
        c2 -= (3 * c3 + 4) >> 3;
        c3 += (3 * c2 + 4) >> 3;
        c2 -= (c1 + 1) >> 1;
        c3 = ((c0 + 1) >> 1) - c3;
        c1 += c2;
        c0 -= c3;
    }

    /// <summary>InvToddodd( ) (Table 163).</summary>
    private static void InvToddodd(ref int c0, ref int c1, ref int c2, ref int c3)
    {
        c3 += c0;
        c2 -= c1;
        var t1 = c3 >> 1;
        var t2 = c2 >> 1;
        c0 -= t1;
        c1 += t2;
        c0 -= (c1 * 3 + 3) >> 3;
        c1 += (c0 * 3 + 3) >> 2;
        c0 -= (c1 * 3 + 4) >> 3;
        c1 -= t2;
        c0 += t1;
        c2 += c1;
        c3 -= c0;
        c1 = -c1;
        c2 = -c2;
    }

    /// <summary>InvPermute2pt( ) (Table 166).</summary>
    public static void Swap(ref int a, ref int b) => (a, b) = (b, a);

    /// <summary>T2pt( ) (Table 167).</summary>
    public static void T2pt(ref int c0, ref int c1)
    {
        c0 -= (c1 + 1) >> 1;
        c1 += c0;
    }

    /// <summary>OverlapPostFilter4x4( ) (Table 168) on 16 samples in raster order.</summary>
    public static void Overlap4x4(Span<int> c)
    {
        T2x2h(ref c[0], ref c[3], ref c[12], ref c[15], 0);
        T2x2h(ref c[1], ref c[2], ref c[13], ref c[14], 0);
        T2x2h(ref c[4], ref c[7], ref c[8], ref c[11], 0);
        T2x2h(ref c[5], ref c[6], ref c[9], ref c[10], 0);
        InvRotate(ref c[13], ref c[12]);
        InvRotate(ref c[9], ref c[8]);
        InvRotate(ref c[7], ref c[3]);
        InvRotate(ref c[6], ref c[2]);
        InvToddoddPost(ref c[10], ref c[11], ref c[14], ref c[15]);
        InvScale(ref c[0], ref c[15]);
        InvScale(ref c[1], ref c[14]);
        InvScale(ref c[4], ref c[11]);
        InvScale(ref c[5], ref c[10]);
        T2x2hPost(ref c[0], ref c[3], ref c[12], ref c[15]);
        T2x2hPost(ref c[1], ref c[2], ref c[13], ref c[14]);
        T2x2hPost(ref c[4], ref c[7], ref c[8], ref c[11]);
        T2x2hPost(ref c[5], ref c[6], ref c[9], ref c[10]);
    }

    /// <summary>OverlapPostFilter4( ) (Table 169).</summary>
    public static void Overlap4(ref int c0, ref int c1, ref int c2, ref int c3)
    {
        c0 += c3;
        c1 += c2;
        c3 -= (c0 + 1) >> 1;
        c2 -= (c1 + 1) >> 1;
        InvScale(ref c0, ref c3);
        InvScale(ref c1, ref c2);
        c0 += (c3 * 3 + 4) >> 3;
        c1 += (c2 * 3 + 4) >> 3;
        c3 -= c0 >> 1;
        c2 -= c1 >> 1;
        c0 += c3;
        c1 += c2;
        c3 = -c3;
        c2 = -c2;
        InvRotate(ref c2, ref c3);
        c3 += (c0 + 1) >> 1;
        c2 += (c1 + 1) >> 1;
        c0 -= c3;
        c1 -= c2;
    }

    /// <summary>OverlapPostFilter2x2( ) (Table 170).</summary>
    public static void Overlap2x2(ref int c0, ref int c1, ref int c2, ref int c3)
    {
        c0 += c3;
        c1 += c2;
        c3 -= (c0 + 1) >> 1;
        c2 -= (c1 + 1) >> 1;
        c1 += (c0 + 2) >> 2;
        c0 += (c1 + 1) >> 1;
        c0 += c1 >> 5;
        c0 += c1 >> 9;
        c0 += c1 >> 13;
        c1 += (c0 + 2) >> 2;
        c3 += (c0 + 1) >> 1;
        c2 += (c1 + 1) >> 1;
        c0 -= c3;
        c1 -= c2;
    }

    /// <summary>OverlapPostFilter2( ) (Table 171).</summary>
    public static void Overlap2(ref int c0, ref int c1)
    {
        c1 += (c0 + 2) >> 2;
        c0 += (c1 + 1) >> 1;
        c0 += c1 >> 5;
        c0 += c1 >> 9;
        c0 += c1 >> 13;
        c1 += (c0 + 2) >> 2;
    }

    /// <summary>InvRotate( ) (Table 172).</summary>
    private static void InvRotate(ref int c0, ref int c1)
    {
        c0 -= (c1 + 1) >> 1;
        c1 += (c0 + 1) >> 1;
    }

    /// <summary>InvScale( ) (Table 173).</summary>
    private static void InvScale(ref int c0, ref int c1)
    {
        c0 += c1;
        c1 = (c0 >> 1) - c1;
        c0 += (c1 * 3 + 0) >> 3;
        c1 += (c0 * 3 + 0) >> 4;
        c1 += c0 >> 7;
        c1 -= c0 >> 10;
    }

    /// <summary>T2x2hPOST( ) (Table 174).</summary>
    private static void T2x2hPost(ref int c0, ref int c1, ref int c2, ref int c3)
    {
        c1 -= c2;
        c0 += (c3 * 3 + 4) >> 3;
        c3 -= c1 >> 1;
        c2 = ((c0 - c1) >> 1) - c2;
        (c2, c3) = (c3, c2);
        c0 -= c3;
        c1 += c2;
    }

    /// <summary>InvToddoddPOST( ) (Table 175).</summary>
    private static void InvToddoddPost(ref int c0, ref int c1, ref int c2, ref int c3)
    {
        c3 += c0;
        c2 -= c1;
        var t1 = c3 >> 1;
        var t2 = c2 >> 1;
        c0 -= t1;
        c1 += t2;
        c0 -= (c1 * 3 + 6) >> 3;
        c1 += (c0 * 3 + 2) >> 2;
        c0 -= (c1 * 3 + 4) >> 3;
        c1 -= t2;
        c0 += t1;
        c2 += c1;
        c3 -= c0;
    }
}
