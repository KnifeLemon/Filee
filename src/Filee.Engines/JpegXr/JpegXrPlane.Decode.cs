// Transform coefficient decoding of a JPEG XR macroblock (ITU-T T.832 9.4–9.8): DC and lowpass prediction and
// dequantization once its lowpass band is parsed (which also sets the highpass scan direction, 9.6.3.2), highpass
// dequantization and prediction once its highpass band (with flexbits) is.

namespace Filee.Engines.JpegXr;

internal sealed partial class JpegXrPlane
{
    /// <summary>DCTransformCoefficientDecoding( ) and LPTransformCoefficientDecoding( ) (Tables 120, 121), then CalcHPPredMode( ).</summary>
    public void DecodeDcLp()
    {
        var mb = _mb;
        var left = mb - 1;
        var top = mb - _mbWidth;
        // DC prediction (9.6.1).
        int dcMode;
        if (LeftEdge && TopEdge)
            dcMode = 3;
        else if (LeftEdge)
            dcMode = 1;
        else if (TopEdge)
            dcMode = 0;
        else
        {
            var topLeft = top - 1;
            int horizontal, vertical;
            if (Color is JpegXrColor.YOnly or JpegXrColor.NComponent)
            {
                horizontal = Abs(Pred(topLeft, 0, 0) - Pred(left, 0, 0));
                vertical = Abs(Pred(topLeft, 0, 0) - Pred(top, 0, 0));
            }
            else
            {
                var scale = Color == JpegXrColor.Yuv420 ? 8 : Color == JpegXrColor.Yuv422 ? 4 : 2;
                horizontal = Abs(Pred(topLeft, 0, 0) - Pred(left, 0, 0)) * scale
                             + Abs(Pred(topLeft, 1, 0) - Pred(left, 1, 0)) + Abs(Pred(topLeft, 2, 0) - Pred(left, 2, 0));
                vertical = Abs(Pred(topLeft, 0, 0) - Pred(top, 0, 0)) * scale
                           + Abs(Pred(topLeft, 1, 0) - Pred(top, 1, 0)) + Abs(Pred(topLeft, 2, 0) - Pred(top, 2, 0));
            }
            dcMode = horizontal * 4 < vertical ? 1 : vertical * 4 < horizontal ? 0 : 2;
        }
        for (var i = 0; i < Components; i++)
        {
            ref var dc = ref Dclp(mb, i, 0);
            if (dcMode == 0)
                dc += Pred(left, i, 0);
            else if (dcMode == 1)
                dc += Pred(top, i, 0);
            else if (dcMode == 2)
                dc += i == 0 || !Subsampled ? (Pred(top, i, 0) + Pred(left, i, 0)) >> 1 : (Pred(top, i, 0) + Pred(left, i, 0) + 1) >> 1;
            Pred(mb, i, 0) = dc;
        }

        // Lowpass prediction (9.6.2).
        var lpMode = dcMode == 0 && _lpIndex[mb] == _lpIndex[left] ? 0
            : dcMode == 1 && _lpIndex[mb] == _lpIndex[top] ? 1
            : 2;
        for (var i = 0; i < Components; i++)
        {
            if (i == 0 || !Subsampled)
            {
                if (lpMode == 0)
                {
                    Dclp(mb, i, 4) += Pred(left, i, 4);
                    Dclp(mb, i, 8) += Pred(left, i, 5);
                    Dclp(mb, i, 12) += Pred(left, i, 6);
                }
                else if (lpMode == 1)
                {
                    Dclp(mb, i, 1) += Pred(top, i, 1);
                    Dclp(mb, i, 2) += Pred(top, i, 2);
                    Dclp(mb, i, 3) += Pred(top, i, 3);
                }
                Pred(mb, i, 1) = Dclp(mb, i, 1);
                Pred(mb, i, 2) = Dclp(mb, i, 2);
                Pred(mb, i, 3) = Dclp(mb, i, 3);
                Pred(mb, i, 4) = Dclp(mb, i, 4);
                Pred(mb, i, 5) = Dclp(mb, i, 8);
                Pred(mb, i, 6) = Dclp(mb, i, 12);
            }
            else if (Color == JpegXrColor.Yuv420)
            {
                if (lpMode == 0)
                    Dclp(mb, i, 2) += Pred(left, i, 2);
                else if (lpMode == 1)
                    Dclp(mb, i, 1) += Pred(top, i, 1);
                Pred(mb, i, 1) = Dclp(mb, i, 1);
                Pred(mb, i, 2) = Dclp(mb, i, 2);
            }
            else
            {
                if (lpMode == 0)
                {
                    Dclp(mb, i, 4) += Pred(left, i, 4);
                    Dclp(mb, i, 2) += Pred(left, i, 2);
                    Dclp(mb, i, 6) += Pred(left, i, 6);
                }
                else if (lpMode == 1)
                {
                    Dclp(mb, i, 4) += Pred(top, i, 4);
                    Dclp(mb, i, 1) += Pred(top, i, 5);
                    Dclp(mb, i, 5) += Dclp(mb, i, 1);
                }
                else if (dcMode == 1)
                {
                    Dclp(mb, i, 5) += Dclp(mb, i, 1);
                }
                Pred(mb, i, 1) = Dclp(mb, i, 1);
                Pred(mb, i, 2) = Dclp(mb, i, 2);
                Pred(mb, i, 4) = Dclp(mb, i, 4);
                Pred(mb, i, 5) = Dclp(mb, i, 5);
                Pred(mb, i, 6) = Dclp(mb, i, 6);
            }
        }

        // CalcHPPredMode (Table 135). Table 122 has it after the dequantization; Windows' codec, which writes most
        // JPEG XR pictures, takes the lowpass coefficients before it, and so does Filee (the scan order follows from it).
        var strHor = Abs(Dclp(mb, 0, 1)) + Abs(Dclp(mb, 0, 2)) + Abs(Dclp(mb, 0, 3));
        var strVer = Abs(Dclp(mb, 0, 4)) + Abs(Dclp(mb, 0, 8)) + Abs(Dclp(mb, 0, 12));
        if (Color is not (JpegXrColor.YOnly or JpegXrColor.NComponent))
        {
            for (var i = 1; i <= 2; i++)
            {
                strHor += Abs(Dclp(mb, i, 1));
                if (Color == JpegXrColor.Yuv420)
                    strVer += Abs(Dclp(mb, i, 2));
                else if (Color == JpegXrColor.Yuv422)
                {
                    strVer += Abs(Dclp(mb, i, 2)) + Abs(Dclp(mb, i, 6));
                    strHor += Abs(Dclp(mb, i, 5));
                }
                else
                    strVer += Abs(Dclp(mb, i, 4));
            }
        }
        _hpMode[mb] = (byte)(strHor * 4 < strVer ? 0 : strVer * 4 < strHor ? 1 : 2);

        // Dequantization (9.8.1, 9.8.2).
        var lp = _lpIndex[mb];
        var dcQp = _tiles.Length > 0 ? _tile.Dc : _planeDc;
        for (var i = 0; i < Components; i++)
        {
            Dclp(mb, i, 0) *= QuantMap(dcQp[i], i == 0 ? 1 : 0);
            var scale = QuantMap(_tile.Lp[i][lp], i == 0 ? 1 : 0);
            var count = i == 0 || !Subsampled ? 15 : Color == JpegXrColor.Yuv422 ? 7 : 3;
            for (var j = 1; j <= count; j++)
                Dclp(mb, i, j) *= scale;
        }
    }

    /// <summary>|value| without Math.Abs's overflow check (a damaged file's sums may wrap around).</summary>
    private static int Abs(int value) => value < 0 ? -value : value;

    /// <summary>The highpass scan direction of a macroblock is vertical (set by <see cref="DecodeDcLp"/>).</summary>
    public bool VerticalScan => _hpMode[_mb] == 1;

    /// <summary>HPMBCoefficientRemap( ), DequantizeHPCoefficients( ) and HPCoefficientPrediction( ) (Tables 125–136).</summary>
    public void DecodeHp()
    {
        var mb = _mb;
        var shift = !_flexed[mb];
        for (var i = 0; i < Components; i++)
        {
            var blocks = Blocks(i) * 4;
            var modelBits = _modelBitsHp[mb * 2 + (i > 0 ? 1 : 0)];
            var scale = QuantMap(_tile.Hp[i][_hpIndex[mb]], 1);
            var samples = Samples[i];
            for (var block = 0; block < blocks; block++)
            {
                for (var k = 1; k < 16; k++)
                {
                    ref var value = ref samples[SampleIndex(i, block, k)];
                    if (Bands >= 2)
                        value = 0;
                    else if (shift)
                        value <<= modelBits;
                    value *= scale;
                }
            }
        }
        var mode = _hpMode[mb];
        if (mode == 2 || Bands >= 2)
            return;
        var full = Subsampled ? 1 : Components;
        for (var i = 0; i < full; i++)
        {
            for (var block = 0; block < 16; block++)
            {
                if (mode == 0 && block % 4 != 0)
                    PredictFrom(i, block, block - 1, 4, 8, 12);
                else if (mode == 1 && block >= 4)
                    PredictFrom(i, block, block - 4, 1, 2, 3);
            }
        }
        if (Color == JpegXrColor.Yuv420)
        {
            for (var i = 1; i <= 2; i++)
            {
                if (mode == 0)
                {
                    PredictFrom(i, 1, 0, 4, 8, 12);
                    PredictFrom(i, 3, 2, 4, 8, 12);
                }
                else
                {
                    PredictFrom(i, 2, 0, 1, 2, 3);
                    PredictFrom(i, 3, 1, 1, 2, 3);
                }
            }
        }
        else if (Color == JpegXrColor.Yuv422)
        {
            for (var i = 1; i <= 2; i++)
            {
                if (mode == 0)
                {
                    foreach (var block in (ReadOnlySpan<int>)[1, 3, 5, 7])
                        PredictFrom(i, block, block - 1, 4, 8, 12);
                }
                else
                {
                    foreach (var block in (ReadOnlySpan<int>)[2, 4, 6, 3, 5, 7])
                        PredictFrom(i, block, block - 2, 1, 2, 3);
                }
            }
        }
    }

    /// <summary>Adds coefficients <paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/> of block <paramref name="from"/> to <paramref name="block"/>'s.</summary>
    private void PredictFrom(int i, int block, int from, int a, int b, int c)
    {
        var samples = Samples[i];
        samples[SampleIndex(i, block, a)] += samples[SampleIndex(i, from, a)];
        samples[SampleIndex(i, block, b)] += samples[SampleIndex(i, from, b)];
        samples[SampleIndex(i, block, c)] += samples[SampleIndex(i, from, c)];
    }
}
