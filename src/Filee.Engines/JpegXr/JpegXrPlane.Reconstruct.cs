// Sample reconstruction of a JPEG XR image plane (ITU-T T.832 9.9): the first level inverse transform of each
// macroblock's DC and lowpass coefficients, the first level overlap filter over them, their combination with the
// highpass coefficients, the second level inverse transform of every 4×4 block, and the second level overlap filter.
//
// The spec writes the overlap filters out tile by tile and edge by edge; geometrically they are one pattern over a
// grid of blocks: a 4×4 (2×2 for 4:2:x chroma at the first level) filter straddling every corner where four blocks
// meet, a 4-point (2-point) filter across each block boundary along the outer two rows and columns (one for 4:2:x
// chroma), and the 2×2 corners, inside one region: the whole plane with soft tiles, each tile with hard tiles. All
// these filters touch disjoint samples, so their order doesn't matter. The 4:2:x chroma corners take the adjacent
// coefficient residual of Tables 154 and 155, at the places those tables name.

namespace Filee.Engines.JpegXr;

internal sealed partial class JpegXrPlane
{
    public void Reconstruct()
    {
        var image = _image;
        Span<int> block = stackalloc int[16];
        for (var i = 0; i < Components; i++)
        {
            var chromaSub = i > 0 && Subsampled;
            // Size of a macroblock's DC-LP block for this component, and the DC-LP array.
            var (uw, uh) = !chromaSub ? (4, 4) : Color == JpegXrColor.Yuv422 ? (2, 4) : (2, 2);
            var dw = image.MbWidth * uw;
            var dh = image.MbHeight * uh;
            var dclp = new int[dw * dh];
            for (var mby = 0; mby < image.MbHeight; mby++)
            {
                for (var mbx = 0; mbx < image.MbWidth; mbx++)
                {
                    var mb = mby * image.MbWidth + mbx;
                    var c = _dclp.AsSpan((mb * Components + i) * 16, 16);
                    FirstLevelTransform(c, i);
                    for (var j = 0; j < uw * uh; j++)
                        dclp[(mby * uh + j / uw) * dw + mbx * uw + j % uw] = c[j];
                }
            }
            if (image.Overlap == 2)
            {
                if (!chromaSub)
                    OverlapGrid(dclp, dw, uw, uh, image);
                else
                    OverlapChroma(dclp, dw, uw, uh, image);
            }
            var width = Widths[i];
            var samples = Samples[i];
            for (var y = 0; y < dh; y++)
            {
                for (var x = 0; x < dw; x++)
                    samples[4 * y * width + 4 * x] = dclp[y * dw + x];
            }
            for (var y = 0; y < Heights[i]; y += 4)
            {
                for (var x = 0; x < width; x += 4)
                {
                    for (var k = 0; k < 16; k++)
                        block[k] = samples[(y + k / 4) * width + x + k % 4];
                    JpegXrTransform.Ict4x4(block);
                    for (var k = 0; k < 16; k++)
                        samples[(y + k / 4) * width + x + k % 4] = block[k];
                }
            }
            if (image.Overlap != 0)
                OverlapGrid(samples, width, chromaSub ? 8 : 16, chromaSub && Color == JpegXrColor.Yuv420 ? 8 : 16, image);
        }
    }

    /// <summary>FirstLevelInverseTransform( ) (Table 151) of one macroblock's DC-LP coefficients.</summary>
    private void FirstLevelTransform(Span<int> c, int i)
    {
        int count;
        if (i == 0 || !Subsampled)
        {
            JpegXrTransform.Ict4x4(c);
            count = 16;
        }
        else if (Color == JpegXrColor.Yuv420)
        {
            JpegXrTransform.T2x2h(ref c[0], ref c[1], ref c[2], ref c[3], 0);
            JpegXrTransform.Swap(ref c[1], ref c[2]);
            count = 4;
        }
        else
        {
            JpegXrTransform.T2pt(ref c[0], ref c[4]);
            JpegXrTransform.T2x2h(ref c[0], ref c[1], ref c[2], ref c[3], 0);
            JpegXrTransform.Swap(ref c[1], ref c[2]);
            JpegXrTransform.T2x2h(ref c[4], ref c[6], ref c[5], ref c[7], 0);
            JpegXrTransform.Swap(ref c[5], ref c[6]);
            count = 8;
        }
        if (i > 0 && Scaled)
        {
            for (var j = 0; j < count; j++)
                c[j] *= 2;
        }
    }

    /// <summary>
    /// The 4×4-block overlap filter over <paramref name="a"/> (rows of <paramref name="stride"/>): a macroblock is
    /// <paramref name="mbWidth"/> × <paramref name="mbHeight"/> samples of it.
    /// </summary>
    private static void OverlapGrid(int[] a, int stride, int mbWidth, int mbHeight, JpegXrHeader image)
    {
        Span<int> c = stackalloc int[16];
        foreach (var (x0, y0, x1, y1) in Regions(image, mbWidth, mbHeight))
        {
            for (var y = y0 + 2; y < y1 - 2; y += 4)
            {
                for (var x = x0 + 2; x < x1 - 2; x += 4)
                {
                    for (var k = 0; k < 16; k++)
                        c[k] = a[(y + k / 4) * stride + x + k % 4];
                    JpegXrTransform.Overlap4x4(c);
                    for (var k = 0; k < 16; k++)
                        a[(y + k / 4) * stride + x + k % 4] = c[k];
                }
            }
            for (var y = y0 + 2; y < y1 - 2; y += 4)
            {
                foreach (var x in (ReadOnlySpan<int>)[x0, x0 + 1, x1 - 2, x1 - 1])
                    Filter4(a, y * stride + x, stride);
            }
            for (var x = x0 + 2; x < x1 - 2; x += 4)
            {
                foreach (var y in (ReadOnlySpan<int>)[y0, y0 + 1, y1 - 2, y1 - 1])
                    Filter4(a, y * stride + x, 1);
            }
            foreach (var (x, y) in (ReadOnlySpan<(int, int)>)[(x0, y0), (x1 - 2, y0), (x0, y1 - 2), (x1 - 2, y1 - 2)])
            {
                var at = y * stride + x;
                JpegXrTransform.Overlap4(ref a[at], ref a[at + 1], ref a[at + stride], ref a[at + stride + 1]);
            }
        }
    }

    private static void Filter4(int[] a, int at, int step) =>
        JpegXrTransform.Overlap4(ref a[at], ref a[at + step], ref a[at + 2 * step], ref a[at + 3 * step]);

    /// <summary>The first level overlap filter of 4:2:0 and 4:2:2 chroma (Tables 154, 155) over 2×2 blocks.</summary>
    private static void OverlapChroma(int[] a, int stride, int mbWidth, int mbHeight, JpegXrHeader image)
    {
        var corners = ResidualCorners(image, mbWidth, mbHeight);
        foreach (var (at, neighbour) in corners)
            a[at] -= a[neighbour];
        foreach (var (x0, y0, x1, y1) in Regions(image, mbWidth, mbHeight))
        {
            for (var y = y0 + 1; y < y1 - 1; y += 2)
            {
                for (var x = x0 + 1; x < x1 - 1; x += 2)
                {
                    var at = y * stride + x;
                    JpegXrTransform.Overlap2x2(ref a[at], ref a[at + 1], ref a[at + stride], ref a[at + stride + 1]);
                }
            }
            for (var y = y0 + 1; y < y1 - 1; y += 2)
            {
                foreach (var x in (ReadOnlySpan<int>)[x0, x1 - 1])
                    JpegXrTransform.Overlap2(ref a[y * stride + x], ref a[(y + 1) * stride + x]);
            }
            for (var x = x0 + 1; x < x1 - 1; x += 2)
            {
                foreach (var y in (ReadOnlySpan<int>)[y0, y1 - 1])
                    JpegXrTransform.Overlap2(ref a[y * stride + x], ref a[y * stride + x + 1]);
            }
        }
        foreach (var (at, neighbour) in corners)
            a[at] += a[neighbour];

        List<(int At, int Neighbour)> ResidualCorners(JpegXrHeader header, int uw, int uh)
        {
            var list = new List<(int, int)>();
            var columns = new List<(int Column, int Neighbour)>
            {
                (0, 1),
                (header.TileLeft[header.TileColumns] * uw - 1, header.TileLeft[header.TileColumns] * uw - 2),
            };
            if (header.HardTiling)
            {
                for (var tx = 1; tx < header.TileColumns - 1; tx++)
                {
                    columns.Add((header.TileLeft[tx] * uw, header.TileLeft[tx] * uw + 1));
                    columns.Add((header.TileLeft[tx] * uw - 1, header.TileLeft[tx] * uw - 2));
                }
            }
            for (var ty = 0; ty < header.TileRows; ty++)
            {
                var rows = new List<int>();
                if (ty == 0 || header.HardTiling)
                    rows.Add(header.TileTop[ty] * uh);
                if (ty == header.TileRows - 1 || header.HardTiling)
                    rows.Add(header.TileTop[ty + 1] * uh - 1);
                foreach (var row in rows)
                {
                    foreach (var (column, neighbour) in columns)
                        list.Add((row * stride + column, row * stride + neighbour));
                }
            }
            return list;
        }
    }

    /// <summary>The regions the overlap filter treats as wholes, in samples: the plane, or each tile with hard tiles.</summary>
    private static IEnumerable<(int X0, int Y0, int X1, int Y1)> Regions(JpegXrHeader image, int mbWidth, int mbHeight)
    {
        if (!image.HardTiling)
        {
            yield return (0, 0, image.MbWidth * mbWidth, image.MbHeight * mbHeight);
            yield break;
        }
        for (var ty = 0; ty < image.TileRows; ty++)
        {
            for (var tx = 0; tx < image.TileColumns; tx++)
                yield return (image.TileLeft[tx] * mbWidth, image.TileTop[ty] * mbHeight, image.TileLeft[tx + 1] * mbWidth, image.TileTop[ty + 1] * mbHeight);
        }
    }
}
