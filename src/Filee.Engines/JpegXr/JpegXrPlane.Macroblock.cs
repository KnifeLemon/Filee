// The macroblock layer of a JPEG XR image plane (ITU-T T.832 8.7.11–8.12): MB_DC, MB_LP, MB_CBPHP, MB_HP, MB_HP_FLEX
// and MB_FLEXBITS with their adaptive state, written as the spec's pseudocode is, followed by each band's decoding
// (9.5–9.8) for the macroblock just parsed.

using System.Numerics;

namespace Filee.Engines.JpegXr;

internal sealed partial class JpegXrPlane
{
    // Per macroblock (index mb = MBy * MBWidth + MBx).
    private int[] _dclp = [];       // MbDCLP[mb][component][16]
    private int[] _pred = [];       // PredDCLP[mb][component][7]
    private byte[] _lpIndex = [];
    private byte[] _hpIndex = [];
    private int[] _cbphp = [];      // MBCBPHP[mb][component]
    private byte[] _modelBitsHp = []; // ModelBitsMBHP[mb][2]
    private byte[] _hpMode = [];

    /// <summary>ImagePlane[component][x + y * width]: highpass coefficients, then the samples.</summary>
    public int[][] Samples { get; private set; } = [];

    public int[] Widths { get; private set; } = [];

    public int[] Heights { get; private set; } = [];

    private int _mbWidth;

    // The macroblock being parsed, and its tile.
    private int _mbx, _mby, _mb;
    private int _tileLeft, _tileRight, _tileTop;

    private bool LeftEdge => _mbx == _tileLeft;

    private bool TopEdge => _mby == _tileTop;

    // Adaptive state (5.5.5–5.5.8, 5.7).
    private readonly JpegXrAdaptive[] _absDc = new JpegXrAdaptive[2];
    private readonly JpegXrAdaptive[] _absLp = new JpegXrAdaptive[2];
    private readonly JpegXrAdaptive[] _absHp = new JpegXrAdaptive[2];
    private readonly JpegXrAdaptive[] _firstLp = new JpegXrAdaptive[2];
    private readonly JpegXrAdaptive[] _firstHp = new JpegXrAdaptive[2];
    private readonly JpegXrAdaptive[] _indexLp = new JpegXrAdaptive[4]; // [chroma * 2 + context]
    private readonly JpegXrAdaptive[] _indexHp = new JpegXrAdaptive[4];
    private JpegXrAdaptive _numCbphp, _numBlkCbphp;
    private readonly JpegXrModel _modelDc = new(), _modelLp = new(), _modelHp = new();
    private readonly int[] _cbphpState = new int[2], _cbphpOnes = new int[2], _cbphpZeroes = new int[2];
    private int _countZeroCbplp, _countMaxCbplp;
    private readonly int[] _lpScan = new int[16], _lpTotals = new int[16];
    private readonly int[] _hpHorScan = new int[16], _hpHorTotals = new int[16];
    private readonly int[] _hpVerScan = new int[16], _hpVerTotals = new int[16];

    /// <summary>Makes room for the plane's macroblocks and samples.</summary>
    public void Allocate()
    {
        _mbWidth = _image.MbWidth;
        var mbs = _image.MbWidth * _image.MbHeight;
        _dclp = new int[mbs * Components * 16];
        _pred = new int[mbs * Components * 7];
        _lpIndex = new byte[mbs];
        _hpIndex = new byte[mbs];
        _cbphp = new int[mbs * Components];
        _modelBitsHp = new byte[mbs * 2];
        _hpMode = new byte[mbs];
        _flexed = new bool[mbs];
        Widths = new int[Components];
        Heights = new int[Components];
        Samples = new int[Components][];
        for (var i = 0; i < Components; i++)
        {
            Widths[i] = i > 0 && Subsampled ? _image.ExtendedWidth / 2 : _image.ExtendedWidth;
            Heights[i] = i > 0 && Color == JpegXrColor.Yuv420 ? _image.ExtendedHeight / 2 : _image.ExtendedHeight;
            Samples[i] = new int[Widths[i] * Heights[i]];
        }
    }

    /// <summary>The macroblock (MBx, MBy) of the current tile is the next one parsed.</summary>
    public void At(int mbx, int mby, int tileLeft, int tileRight, int tileTop)
    {
        (_mbx, _mby, _mb) = (mbx, mby, mby * _mbWidth + mbx);
        (_tileLeft, _tileRight, _tileTop) = (tileLeft, tileRight, tileTop);
    }

    public int Macroblock => _mb;

    private bool ResetContext => _mbx == _tileRight - 1 || (_mbx - _tileLeft) % 16 == 0;

    private ref int Dclp(int mb, int component, int j) => ref _dclp[(mb * Components + component) * 16 + j];

    private ref int Pred(int mb, int component, int j) => ref _pred[(mb * Components + component) * 7 + j];

    // ───────────────────────── DC (8.7.11–8.7.14) ─────────────────────────

    public void MbDc(JpegXrBits bits)
    {
        if (LeftEdge && TopEdge)
        {
            _absDc[0].Initialize1();
            _absDc[1].Initialize1();
            _modelDc.Initialize(0);
        }
        Span<int> lapMean = [0, 0];
        if (!Yuv)
        {
            for (var n = 0; n < Components; n++)
            {
                var absLevel = bits.Flag();
                var m = n == 0 ? 0 : 1;
                if (absLevel)
                    lapMean[m]++;
                Dclp(_mb, n, 0) = DecodeDc(bits, _modelDc.Bits[m], chroma: false, absLevel);
            }
        }
        else
        {
            var yuv = bits.Code(JpegXrTables.ValDcYuv);
            for (var n = 0; n < 3; n++)
            {
                var absLevel = (yuv & (4 >> n)) != 0;
                if (absLevel)
                    lapMean[n == 0 ? 0 : 1]++;
                Dclp(_mb, n, 0) = DecodeDc(bits, _modelDc.Bits[n == 0 ? 0 : 1], chroma: n > 0, absLevel);
            }
        }
        UpdateModel(lapMean, _modelDc, 0);
        if (ResetContext)
        {
            _absDc[0].Adapt1();
            _absDc[1].Adapt1();
        }
    }

    /// <summary>DECODE_DC( ) (Table 49).</summary>
    private int DecodeDc(JpegXrBits bits, int modelBits, bool chroma, bool absLevel)
    {
        var dc = 0;
        if (absLevel)
            dc = DecodeAbsLevel(bits, ref _absDc[chroma ? 1 : 0]) - 1;
        if (modelBits > 0)
            dc = (dc << modelBits) | bits.Int(modelBits);
        if (dc != 0 && bits.Flag())
            dc = -dc;
        return dc;
    }

    /// <summary>DECODE_ABS_LEVEL( ) (Table 50) with the given adaptive structure.</summary>
    private static int DecodeAbsLevel(JpegXrBits bits, ref JpegXrAdaptive adaptive)
    {
        ReadOnlySpan<int> remap = [2, 3, 4, 6, 10, 14];
        ReadOnlySpan<int> fixedLength = [0, 0, 1, 2, 2, 2];
        var index = bits.Code(JpegXrTables.AbsLevelIndex[adaptive.Table]);
        adaptive.Discrim1 += JpegXrTables.AbsLevelIndexDelta[index];
        if (index < 6)
        {
            var level = remap[index];
            if (fixedLength[index] > 0)
                level += bits.Int(fixedLength[index]);
            return level;
        }
        var width = bits.Int(4) + 4;
        if (width == 19)
        {
            width += bits.Int(2);
            if (width == 22)
                width += bits.Int(3);
        }
        // A level of more than 30 bits is no coefficient of any picture.
        if (width > 30)
            throw JpegXrBits.Damaged();
        return 2 + (1 << width) + bits.Int(width);
    }

    // ───────────────────────── Lowpass (8.7.16) ─────────────────────────

    public void MbLp(JpegXrBits bits)
    {
        if (LeftEdge && TopEdge)
        {
            _countZeroCbplp = _countMaxCbplp = 1;
            _firstLp[0].Initialize2();
            _firstLp[1].Initialize2();
            for (var i = 0; i < 4; i++)
                _indexLp[i].Initialize2();
            _absLp[0].Initialize1();
            _absLp[1].Initialize1();
            for (var i = 1; i <= 15; i++)
                (_lpScan[i], _lpTotals[i]) = (JpegXrTables.ScanOrder0[i], JpegXrTables.ScanTotals[i]);
            _modelLp.Initialize(1);
        }
        if ((_mbx - _tileLeft) % 16 == 0)
        {
            for (var i = 1; i <= 15; i++)
                _lpTotals[i] = JpegXrTables.ScanTotals[i];
        }
        Span<int> lapMean = [0, 0];
        var fullPlanes = Subsampled ? 2 : Components;
        int cbplp;
        if (Yuv)
        {
            var max = fullPlanes * 4 - 5;
            if (_countZeroCbplp <= 0 || _countMaxCbplp < 0)
            {
                var code = bits.Code(Color == JpegXrColor.Yuv444 ? JpegXrTables.CbplpYuv444 : JpegXrTables.CbplpYuv42x);
                cbplp = _countMaxCbplp < _countZeroCbplp ? max - code : code;
            }
            else
            {
                cbplp = bits.Int(fullPlanes);
            }
            _countZeroCbplp = Math.Clamp(_countZeroCbplp + 1 - (cbplp == 0 ? 4 : 0), -8, 7);
            _countMaxCbplp = Math.Clamp(_countMaxCbplp + 1 - (cbplp == max ? 4 : 0), -8, 7);
        }
        else
        {
            cbplp = 0;
            for (var n = 0; n < Components; n++)
                cbplp |= bits.Int(1) << n;
        }
        for (var n = 0; n < Components; n++)
        {
            for (var j = 1; j < 16; j++)
                Dclp(_mb, n, j) = 0;
        }
        Span<int> runLevels = stackalloc int[32];
        Span<int> temp = stackalloc int[16];
        for (var n = 0; n < fullPlanes; n++)
        {
            var index = n == 0 ? 0 : 1;
            var nonZero = 0;
            if (((cbplp >> n) & 1) != 0)
            {
                runLevels.Clear();
                var location = 1;
                if (Color == JpegXrColor.Yuv420 && n > 0)
                    location = 10;
                if (Color == JpegXrColor.Yuv422 && n > 0)
                    location = 2;
                nonZero = DecodeBlock(bits, index == 1, runLevels, band: 1, location);
                if (Subsampled && n > 0)
                {
                    // U and V interleaved, in a fixed order.
                    ReadOnlySpan<int> remapArr = [4, 1, 2, 3, 5, 6, 7];
                    temp.Clear();
                    var offset = Color == JpegXrColor.Yuv420 ? 1 : 0;
                    var count = Color == JpegXrColor.Yuv422 ? 14 : 6;
                    var i = 0;
                    for (var k = 0; k < nonZero; k++)
                    {
                        i += runLevels[k * 2];
                        if (i >= count)
                            throw JpegXrBits.Damaged();
                        temp[i] = runLevels[k * 2 + 1];
                        i++;
                    }
                    for (var k = 0; k < count; k++)
                    {
                        var remap = remapArr[(k >> 1) + offset];
                        var at = Color == JpegXrColor.Yuv420 ? JpegXrTables.Transpose420[remap] : JpegXrTables.Transpose422[remap];
                        Dclp(_mb, (k & 1) + 1, at) = temp[k];
                    }
                }
                else
                {
                    var i = 1;
                    for (var k = 0; k < nonZero; k++)
                    {
                        i += runLevels[k * 2];
                        if (i > 15)
                            throw JpegXrBits.Damaged();
                        // AdaptiveLPScan (Table 113).
                        Dclp(_mb, n, _lpScan[i]) = runLevels[k * 2 + 1];
                        _lpTotals[i]++;
                        if (i > 1 && _lpTotals[i] > _lpTotals[i - 1])
                        {
                            (_lpTotals[i], _lpTotals[i - 1]) = (_lpTotals[i - 1], _lpTotals[i]);
                            (_lpScan[i], _lpScan[i - 1]) = (_lpScan[i - 1], _lpScan[i]);
                        }
                        i++;
                    }
                }
            }
            var modelBits = _modelLp.Bits[index];
            lapMean[index] += nonZero;
            if (modelBits > 0)
            {
                if (Color == JpegXrColor.Yuv420 && n > 0)
                {
                    for (var k = 1; k < 4; k++)
                    {
                        RefineLp(bits, ref Dclp(_mb, 1, JpegXrTables.Transpose420[k]), modelBits);
                        RefineLp(bits, ref Dclp(_mb, 2, JpegXrTables.Transpose420[k]), modelBits);
                    }
                }
                else if (Color == JpegXrColor.Yuv422 && n > 0)
                {
                    for (var k = 1; k < 8; k++)
                    {
                        RefineLp(bits, ref Dclp(_mb, 1, JpegXrTables.Transpose422[k]), modelBits);
                        RefineLp(bits, ref Dclp(_mb, 2, JpegXrTables.Transpose422[k]), modelBits);
                    }
                }
                else
                {
                    for (var k = 1; k < 16; k++)
                        RefineLp(bits, ref Dclp(_mb, n, JpegXrTables.Transpose444[k]), modelBits);
                }
            }
        }
        UpdateModel(lapMean, _modelLp, 1);
        if (ResetContext)
        {
            _firstLp[0].Adapt2(4);
            _indexLp[0].Adapt2(3);
            _indexLp[1].Adapt2(3);
            _firstLp[1].Adapt2(4);
            _indexLp[2].Adapt2(3);
            _indexLp[3].Adapt2(3);
            _absLp[0].Adapt1();
            _absLp[1].Adapt1();
        }
    }

    /// <summary>REFINE_LP( ) (Table 54).</summary>
    private static void RefineLp(JpegXrBits bits, ref int coefficient, int modelBits)
    {
        var refinement = bits.Int(modelBits);
        if (coefficient > 0)
            coefficient = (coefficient << modelBits) + refinement;
        else if (coefficient < 0)
            coefficient = (coefficient << modelBits) - refinement;
        else
        {
            coefficient = refinement;
            if (coefficient != 0 && bits.Flag())
                coefficient = -coefficient;
        }
    }

    // ───────────────────────── Blocks (8.7.18.5–8.7.18.8) ─────────────────────────

    /// <summary>DECODE_BLOCK( ) (Table 72): run/level pairs into <paramref name="coefficients"/>; returns their count.</summary>
    private int DecodeBlock(JpegXrBits bits, bool chroma, Span<int> coefficients, int band, int location)
    {
        var nonZero = 1;
        var first = DecodeFirstIndex(bits, chroma, band);
        var sign = bits.Flag();
        var sr = first & 1;
        var srn = first >> 2;
        var context = sr & srn;
        var absolute = band == 1 ? _absLp : _absHp;
        coefficients[1] = (first & 2) != 0 ? DecodeAbsLevel(bits, ref absolute[context]) : 1;
        if (sign)
            coefficients[1] = -coefficients[1];
        coefficients[0] = sr == 0 ? DecodeRun(bits, 15 - location) : 0;
        location += coefficients[0] + 1;
        while (srn != 0)
        {
            if (nonZero >= 16)
                throw JpegXrBits.Damaged();
            sr = srn & 1;
            coefficients[nonZero * 2] = sr == 0 ? DecodeRun(bits, 15 - location) : 0;
            location += coefficients[nonZero * 2] + 1;
            var index = DecodeIndex(bits, location, chroma, band, context);
            srn = index >> 1;
            context &= srn;
            sign = bits.Flag();
            var level = (index & 1) != 0 ? DecodeAbsLevel(bits, ref absolute[context]) : 1;
            coefficients[nonZero * 2 + 1] = sign ? -level : level;
            nonZero++;
        }
        return nonZero;
    }

    /// <summary>DECODE_RUN( ) (Table 73).</summary>
    private static int DecodeRun(JpegXrBits bits, int maxRun)
    {
        ReadOnlySpan<int> remap = [1, 2, 3, 5, 7, 1, 2, 3, 5, 7, 1, 2, 3, 4, 5];
        ReadOnlySpan<int> runBin = [-1, -1, -1, -1, 2, 2, 2, 1, 1, 1, 1, 0, 0, 0, 0];
        ReadOnlySpan<int> runFixed = [0, 0, 1, 1, 3, 0, 0, 1, 1, 2, 0, 0, 0, 0, 1];
        if (maxRun < 1 || maxRun > 14)
            throw JpegXrBits.Damaged();
        if (maxRun < 5)
            return maxRun == 1 ? 1 : bits.Code(JpegXrTables.RunValue[maxRun]!);
        var index = bits.Code(JpegXrTables.RunIndex) + 5 * runBin[maxRun];
        var run = remap[index];
        if (runFixed[index] > 0)
            run += bits.Int(runFixed[index]);
        return run;
    }

    /// <summary>DECODE_INDEX( ) (Table 74).</summary>
    private int DecodeIndex(JpegXrBits bits, int location, bool chroma, int band, int context)
    {
        if (location < 15)
        {
            ref var adaptive = ref (band == 1 ? _indexLp : _indexHp)[(chroma ? 2 : 0) + context];
            var index = bits.Code(JpegXrTables.IndexA[adaptive.Table]);
            adaptive.Discrim1 += JpegXrTables.Index1Delta[adaptive.Delta][index];
            adaptive.Discrim2 += JpegXrTables.Index1Delta[adaptive.Delta2][index];
            return index;
        }
        if (location == 15)
            return bits.Code(JpegXrTables.IndexB);
        if (location == 16)
            return bits.Int(1);
        throw JpegXrBits.Damaged();
    }

    /// <summary>DECODE_FIRST_INDEX( ) (Table 75).</summary>
    private int DecodeFirstIndex(JpegXrBits bits, bool chroma, int band)
    {
        ref var adaptive = ref (band == 1 ? _firstLp : _firstHp)[chroma ? 1 : 0];
        var first = bits.Code(JpegXrTables.FirstIndex[adaptive.Table]);
        adaptive.Discrim1 += JpegXrTables.FirstIndexDelta[adaptive.Delta][first];
        adaptive.Discrim2 += JpegXrTables.FirstIndexDelta[adaptive.Delta2][first];
        return first;
    }

    // ───────────────────────── Coded block pattern (8.7.17, 8.10) ─────────────────────────

    public void MbCbphp(JpegXrBits bits)
    {
        ReadOnlySpan<int> flc = [0, 2, 1, 2, 2, 0];
        ReadOnlySpan<int> off = [0, 4, 2, 8, 12, 1];
        ReadOnlySpan<int> output = [0, 15, 3, 12, 1, 2, 4, 8, 5, 6, 9, 10, 7, 11, 13, 14];
        Span<int> diff = stackalloc int[MaxComponents];
        if (LeftEdge && TopEdge)
        {
            _numCbphp.Initialize1();
            _numBlkCbphp.Initialize1();
        }
        var groups = Color is JpegXrColor.Yuvk or JpegXrColor.NComponent ? Components : 1;
        for (var i = 0; i < groups; i++)
        {
            var count = bits.Code(JpegXrTables.NumCbphp[_numCbphp.Table]);
            _numCbphp.Discrim1 += JpegXrTables.NumCbphpDelta[count];
            var cbphp = RefineCbphp(bits, count);
            for (var block = 0; block < 4; block++)
            {
                if ((cbphp & (1 << block)) == 0)
                    continue;
                int value;
                if (Yuv)
                {
                    value = bits.Code(JpegXrTables.NumBlkCbphpYuv[_numBlkCbphp.Table]);
                    _numBlkCbphp.Discrim1 += JpegXrTables.NumBlkCbphpYuvDelta[value];
                }
                else
                {
                    value = bits.Code(JpegXrTables.NumCbphp[_numBlkCbphp.Table]);
                    _numBlkCbphp.Discrim1 += JpegXrTables.NumCbphpDelta[value];
                }
                var val = value + 1;
                var blockCbphp = 0;
                if (val >= 6)
                {
                    blockCbphp = 0x10 * (bits.Code(JpegXrTables.ChrCbphp) + 1);
                    if (val >= 9)
                        val += bits.Code(JpegXrTables.ChrCbphp);
                    val -= 6;
                }
                if (val > 5)
                    throw JpegXrBits.Damaged();
                var code = off[val];
                if (flc[val] > 0)
                    code += bits.Int(flc[val]);
                blockCbphp += output[code];
                switch (Color)
                {
                    case JpegXrColor.Yuv444:
                        diff[0] |= (blockCbphp & 0x0F) << (block * 4);
                        for (var k = 0; k < 2; k++)
                        {
                            if (((blockCbphp >> (k + 4)) & 1) != 0)
                                diff[k + 1] |= RefineCbphp(bits, bits.Code(JpegXrTables.NumChBlk) + 1) << (block * 4);
                        }
                        break;
                    case JpegXrColor.Yuv422:
                        {
                            ReadOnlySpan<int> shift = [0, 1, 4, 5];
                            diff[0] |= (blockCbphp & 0x0F) << (block * 4);
                            for (var k = 0; k < 2; k++)
                            {
                                if (((blockCbphp >> (k + 4)) & 1) != 0)
                                    diff[k + 1] |= shift[bits.Code(JpegXrTables.ChrCbphp) + 1] << shift[block];
                            }
                            break;
                        }
                    case JpegXrColor.Yuv420:
                        diff[0] |= (blockCbphp & 0x0F) << (block * 4);
                        diff[1] |= ((blockCbphp >> 4) & 1) << block;
                        diff[2] |= ((blockCbphp >> 5) & 1) << block;
                        break;
                    default:
                        diff[i] |= blockCbphp << (block * 4);
                        break;
                }
            }
        }
        PredictCbphp(diff);
    }

    /// <summary>REFINE_CBPHP( ) (Table 58).</summary>
    private static int RefineCbphp(JpegXrBits bits, int count) => count switch
    {
        2 => bits.Code(JpegXrTables.RefCbphp1),
        1 => 1 << bits.Int(2),
        3 => 0x0F ^ (1 << bits.Int(2)),
        4 => 0x0F,
        _ => 0,
    };

    /// <summary>PredCBPHP( ) (Tables 65–68).</summary>
    private void PredictCbphp(Span<int> diff)
    {
        if (LeftEdge && TopEdge)
        {
            _cbphpState[0] = _cbphpState[1] = 0;
            _cbphpOnes[0] = _cbphpOnes[1] = -4;
            _cbphpZeroes[0] = _cbphpZeroes[1] = 4;
        }
        var full = Subsampled ? 1 : Components;
        for (var i = 0; i < full; i++)
            _cbphp[_mb * Components + i] = Predict444(i, diff[i]);
        if (Subsampled)
        {
            for (var i = 1; i <= 2; i++)
                _cbphp[_mb * Components + i] = Color == JpegXrColor.Yuv422 ? Predict422(i, diff[i]) : Predict420(i, diff[i]);
        }
    }

    private int Neighbour(int component, int fromTop, int fromLeft)
    {
        if (LeftEdge)
            return TopEdge ? 1 : (_cbphp[(_mb - _mbWidth) * Components + component] >> fromTop) & 1;
        return (_cbphp[(_mb - 1) * Components + component] >> fromLeft) & 1;
    }

    private int Predict444(int i, int cbphp)
    {
        var c = i > 0 ? 1 : 0;
        if (_cbphpState[c] == 0)
        {
            cbphp ^= Neighbour(i, 10, 5);
            cbphp ^= 0x02 & (cbphp << 1);
            cbphp ^= 0x10 & (cbphp << 3);
            cbphp ^= 0x20 & (cbphp << 1);
            cbphp ^= (cbphp & 0x33) << 2;
            cbphp ^= (cbphp & 0x00CC) << 6;
            cbphp ^= (cbphp & 0x3300) << 2;
        }
        else if (_cbphpState[c] == 2)
        {
            cbphp ^= 0x0000FFFF;
        }
        UpdateCbphpModel(c, BitOperations.PopCount((uint)cbphp));
        return cbphp;
    }

    private int Predict422(int i, int cbphp)
    {
        if (_cbphpState[1] == 0)
        {
            cbphp ^= Neighbour(i, 6, 1);
            cbphp ^= (cbphp & 0x01) << 1;
            cbphp ^= (cbphp & 0x03) << 2;
            cbphp ^= (cbphp & 0x0C) << 2;
            cbphp ^= (cbphp & 0x30) << 2;
        }
        else if (_cbphpState[1] == 2)
        {
            cbphp ^= 0x00FF;
        }
        UpdateCbphpModel(1, BitOperations.PopCount((uint)cbphp) * 2);
        return cbphp;
    }

    private int Predict420(int i, int cbphp)
    {
        if (_cbphpState[1] == 0)
        {
            cbphp ^= Neighbour(i, 2, 1);
            cbphp ^= 0x02 & (cbphp << 1);
            cbphp ^= (cbphp & 0x3) << 2;
        }
        else if (_cbphpState[1] == 2)
        {
            cbphp ^= 0x0F;
        }
        UpdateCbphpModel(1, BitOperations.PopCount((uint)cbphp) * 4);
        return cbphp;
    }

    /// <summary>UpdateCBPHPModel( ) (Table 106).</summary>
    private void UpdateCbphpModel(int i, int ones)
    {
        const int Diff = 3;
        _cbphpOnes[i] = Math.Clamp(_cbphpOnes[i] + ones - Diff, -16, 15);
        _cbphpZeroes[i] = Math.Clamp(_cbphpZeroes[i] + 16 - ones - Diff, -16, 15);
        if (_cbphpOnes[i] < 0)
            _cbphpState[i] = _cbphpOnes[i] < _cbphpZeroes[i] ? 1 : 2;
        else if (_cbphpZeroes[i] < 0)
            _cbphpState[i] = 2;
        else
            _cbphpState[i] = 0;
    }

    // ───────────────────────── Highpass (8.7.18, 8.7.19) ─────────────────────────

    /// <summary>MB_HP( ), or MB_HP_FLEX( ) when <paramref name="flex"/> (spatial mode reads flexbits right after).</summary>
    public void MbHp(JpegXrBits bits, bool flex, int trimFlexbits)
    {
        if (LeftEdge && TopEdge)
        {
            _firstHp[0].Initialize2();
            _firstHp[1].Initialize2();
            for (var i = 0; i < 4; i++)
                _indexHp[i].Initialize2();
            _absHp[0].Initialize1();
            _absHp[1].Initialize1();
            for (var i = 1; i <= 15; i++)
            {
                (_hpHorScan[i], _hpVerScan[i]) = (JpegXrTables.ScanOrder0[i], JpegXrTables.ScanOrder1[i]);
                _hpHorTotals[i] = _hpVerTotals[i] = JpegXrTables.ScanTotals[i];
            }
            _modelHp.Initialize(2);
        }
        if ((_mbx - _tileLeft) % 16 == 0)
        {
            for (var i = 1; i <= 15; i++)
                _hpHorTotals[i] = _hpVerTotals[i] = JpegXrTables.ScanTotals[i];
        }
        Span<int> lapMean = [0, 0];
        Span<int> local = stackalloc int[32];
        var vertical = _hpMode[_mb] == 1;
        for (var i = 0; i < Components; i++)
        {
            var chroma = i > 0;
            var modelBits = _modelHp.Bits[chroma ? 1 : 0];
            var blocks = Blocks(i);
            var cbphp = _cbphp[_mb * Components + i];
            for (var block = 0; block < blocks * 4; block++)
            {
                var map = blocks == 4 ? JpegXrTables.HierScanOrder[block] : block;
                ClearBlock(i, map);
                var nonZero = 0;
                if ((cbphp & 1) != 0)
                {
                    local.Clear();
                    nonZero = DecodeBlock(bits, chroma, local, band: 2, location: 1);
                    var k = 1;
                    for (var n = 0; n < nonZero; n++)
                    {
                        k += local[n * 2];
                        if (k > 15)
                            throw JpegXrBits.Damaged();
                        HpScan(i, map, k, local[n * 2 + 1], vertical);
                        k++;
                    }
                }
                if (flex && Bands == 0)
                    BlockFlexbits(bits, i, map, modelBits, trimFlexbits);
                lapMean[chroma ? 1 : 0] += nonZero;
                cbphp >>= 1;
            }
        }
        _modelBitsHp[_mb * 2] = (byte)_modelHp.Bits[0];
        _modelBitsHp[_mb * 2 + 1] = (byte)_modelHp.Bits[1];
        UpdateModel(lapMean, _modelHp, 2);
        if (ResetContext)
        {
            _firstHp[0].Adapt2(4);
            _indexHp[0].Adapt2(3);
            _indexHp[1].Adapt2(3);
            _firstHp[1].Adapt2(4);
            _indexHp[2].Adapt2(3);
            _indexHp[3].Adapt2(3);
            _absHp[0].Adapt1();
            _absHp[1].Adapt1();
            _numCbphp.Adapt1();
            _numBlkCbphp.Adapt1();
        }
    }

    /// <summary>4×4 blocks per row and column of component <paramref name="i"/>'s macroblock, times four: 4, 2 or 1.</summary>
    private int Blocks(int i) => i > 0 && Color == JpegXrColor.Yuv420 ? 1 : i > 0 && Color == JpegXrColor.Yuv422 ? 2 : 4;

    /// <summary>Where coefficient <paramref name="k"/> of block <paramref name="block"/> of the macroblock lies in the plane (9.9.4).</summary>
    private int SampleIndex(int i, int block, int k)
    {
        int x, y;
        switch (Blocks(i))
        {
            case 4:
                x = 16 * _mbx + 4 * (block % 4) + k % 4;
                y = 16 * _mby + 4 * (block / 4) + k / 4;
                break;
            case 2:
                x = 8 * _mbx + 4 * (block % 2) + k % 4;
                y = 16 * _mby + 4 * (block / 2) + k / 4;
                break;
            default:
                x = 8 * _mbx + 4 * (block % 2) + k % 4;
                y = 8 * _mby + 4 * (block / 2) + k / 4;
                break;
        }
        return y * Widths[i] + x;
    }

    private void ClearBlock(int i, int block)
    {
        for (var k = 1; k < 16; k++)
            Samples[i][SampleIndex(i, block, k)] = 0;
    }

    /// <summary>AdaptiveHPScan( ) (Table 114): the VLC part of a coefficient into the plane.</summary>
    private void HpScan(int i, int block, int position, int value, bool vertical)
    {
        var scan = vertical ? _hpVerScan : _hpHorScan;
        var totals = vertical ? _hpVerTotals : _hpHorTotals;
        Samples[i][SampleIndex(i, block, scan[position])] = value;
        totals[position]++;
        if (position > 1 && totals[position] > totals[position - 1])
        {
            (totals[position], totals[position - 1]) = (totals[position - 1], totals[position]);
            (scan[position], scan[position - 1]) = (scan[position - 1], scan[position]);
        }
    }

    /// <summary>MB_FLEXBITS( ) (Table 83), in a frequency mode flexbits tile.</summary>
    public void MbFlexbits(JpegXrBits bits, int trimFlexbits)
    {
        for (var i = 0; i < Components; i++)
        {
            var modelBits = _modelBitsHp[_mb * 2 + (i > 0 ? 1 : 0)];
            var blocks = Blocks(i);
            for (var block = 0; block < blocks * 4; block++)
                BlockFlexbits(bits, i, blocks == 4 ? JpegXrTables.HierScanOrder[block] : block, modelBits, trimFlexbits);
        }
    }

    /// <summary>
    /// BLOCK_FLEXBITS( ) and DECODE_FLEX( ) (Tables 84, 85) and the remapping that joins the flexbits to the VLC part
    /// (Table 126): the plane then holds MBBuffer's value for each coefficient of the block.
    /// </summary>
    private void BlockFlexbits(JpegXrBits bits, int i, int block, int modelBits, int trim)
    {
        var left = Math.Max(0, modelBits - trim);
        var samples = Samples[i];
        for (var n = 1; n < 16; n++)
        {
            var at = SampleIndex(i, block, JpegXrTables.Transpose444[n]);
            var vlc = samples[at];
            var flex = 0;
            if (left > 0)
            {
                var refinement = bits.Int(left);
                if (vlc > 0)
                    flex = refinement;
                else if (vlc < 0)
                    flex = -refinement;
                else
                {
                    flex = refinement;
                    if (flex != 0 && bits.Flag())
                        flex = -flex;
                }
                flex <<= trim;
            }
            samples[at] = (vlc << modelBits) + flex;
        }
        _flexed[_mb] = true;
    }

    /// <summary>Macroblocks whose highpass coefficients already have their flexbits (MBBuffer's values).</summary>
    private bool[] _flexed = [];

    /// <summary>UpdateModelMB( ) (Table 116).</summary>
    private void UpdateModel(Span<int> lapMean, JpegXrModel model, int band)
    {
        const int ModelWeight = 70;
        lapMean[0] *= Weight0[band];
        if (Color == JpegXrColor.Yuv420)
            lapMean[1] *= Weight2[band];
        else if (Color == JpegXrColor.Yuv422)
            lapMean[1] *= Weight2[3 + band];
        else
        {
            lapMean[1] *= Weight1[band][Math.Min(Components, 16) - 1];
            if (band == 2)
                lapMean[1] >>= 4;
        }
        var models = Color == JpegXrColor.YOnly ? 1 : 2;
        for (var j = 0; j < models; j++)
        {
            var state = model.State[j];
            var delta = (lapMean[j] - ModelWeight) >> 2;
            if (delta <= -8)
            {
                delta = Math.Max(delta + 4, -16);
                state += delta;
                if (state < -8)
                {
                    if (model.Bits[j] == 0)
                        state = -8;
                    else
                    {
                        state = 0;
                        model.Bits[j]--;
                    }
                }
            }
            else if (delta >= 8)
            {
                delta = Math.Min(delta - 4, 15);
                state += delta;
                if (state > 8)
                {
                    if (model.Bits[j] >= 15)
                    {
                        model.Bits[j] = 15;
                        state = 8;
                    }
                    else
                    {
                        state = 0;
                        model.Bits[j]++;
                    }
                }
            }
            model.State[j] = state;
        }
    }
}
