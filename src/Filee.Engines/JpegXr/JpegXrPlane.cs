// One image plane of a JPEG XR codestream (the primary plane or the alpha plane, ITU-T T.832 clause 8 and 9.4–9.8):
// its IMAGE_PLANE_HEADER, the adaptive parsing state (VLC table choice, coefficient normalization, scan orders, CBP
// models), and the macroblock syntax of all four bands. Each macroblock's DC and lowpass coefficients are predicted
// and dequantized as soon as they are parsed, since the highpass scan order of the macroblock depends on them
// (9.6.3.2); highpass coefficients go straight into the plane's sample array at the places the second level transform
// takes them from (9.9.4), and are predicted and dequantized once their flexbits are in.

namespace Filee.Engines.JpegXr;

internal enum JpegXrColor
{
    YOnly = 0,
    Yuv420 = 1,
    Yuv422 = 2,
    Yuv444 = 3,
    Yuvk = 4,
    NComponent = 6,
}

/// <summary>An AdaptiveVLC data structure (5.5.5).</summary>
internal struct JpegXrAdaptive
{
    public int Discrim1;
    public int Discrim2;
    public int Table;
    public int Delta;
    public int Delta2;

    public void Initialize1() => (Table, Delta, Discrim1) = (0, 0, 0);

    public void Initialize2() => (Table, Delta, Delta2, Discrim1, Discrim2) = (1, 0, 1, 0, 0);

    /// <summary>AdaptVLCTable1 (Table 101).</summary>
    public void Adapt1()
    {
        if (Discrim1 < -8 && Table != 0)
            (Table, Discrim1) = (Table - 1, 0);
        else if (Discrim1 > 8 && Table != 1)
            (Table, Discrim1) = (Table + 1, 0);
        else
            Discrim1 = Math.Clamp(Discrim1, -64, 64);
    }

    /// <summary>AdaptVLCTable2 (Table 102).</summary>
    public void Adapt2(int maxTable)
    {
        var change = false;
        if (Discrim1 < -8 && Table != 0)
            (Table, change) = (Table - 1, true);
        else if (Discrim2 > 8 && Table != maxTable)
            (Table, change) = (Table + 1, true);
        if (change)
        {
            (Discrim1, Discrim2) = (0, 0);
            if (Table == maxTable)
                (Delta, Delta2) = (Table - 1, Table - 1);
            else if (Table == 0)
                (Delta, Delta2) = (Table, Table);
            else
                (Delta, Delta2) = (Table - 1, Table);
        }
        else
        {
            Discrim1 = Math.Clamp(Discrim1, -64, 64);
            Discrim2 = Math.Clamp(Discrim2, -64, 64);
        }
    }
}

/// <summary>The Model data structure of adaptive coefficient normalization (5.5.6).</summary>
internal sealed class JpegXrModel
{
    public readonly int[] State = new int[2];
    public readonly int[] Bits = new int[2];

    /// <summary>InitializeModelMB (Table 115).</summary>
    public void Initialize(int band)
    {
        State[0] = State[1] = 0;
        Bits[0] = Bits[1] = (2 - band) * 4;
    }
}

internal sealed partial class JpegXrPlane
{
    private static readonly int[] Weight0 = [240, 12, 1];

    private static readonly int[][] Weight1 =
    [
        [0, 240, 120, 80, 60, 48, 40, 34, 30, 27, 24, 22, 20, 18, 17, 16],
        [0, 12, 6, 4, 3, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1],
        [0, 16, 8, 5, 4, 3, 3, 2, 2, 2, 2, 1, 1, 1, 1, 1],
    ];

    private static readonly int[] Weight2 = [120, 37, 2, 120, 18, 1];

    private readonly JpegXrHeader _image;
    private readonly bool _alpha;

    public JpegXrPlane(JpegXrHeader image, bool alpha)
    {
        _image = image;
        _alpha = alpha;
    }

    // ───────────────────────── Image plane header (8.4) ─────────────────────────

    public JpegXrColor Color { get; private set; }

    public bool Scaled { get; private set; }

    /// <summary>BANDS_PRESENT: 0 all, 1 no flexbits, 2 no highpass, 3 DC only.</summary>
    public int Bands { get; private set; }

    public int NumBands => 4 - Bands;

    public int ChromaCenteringX { get; private set; }

    public int ChromaCenteringY { get; private set; }

    public int Components { get; private set; }

    public int ShiftBits { get; private set; }

    public int LenMantissa { get; private set; }

    public int ExpBias { get; private set; }

    private bool _dcUniform, _lpUniform, _hpUniform;

    /// <summary>Joint chroma: YUV formats code chroma DC, CBPHP and (4:2:x) lowpass with the luma.</summary>
    private bool Yuv => Color is JpegXrColor.Yuv420 or JpegXrColor.Yuv422 or JpegXrColor.Yuv444;

    private bool Subsampled => Color is JpegXrColor.Yuv420 or JpegXrColor.Yuv422;

    public void ReadHeader(JpegXrBits bits)
    {
        var color = bits.Int(3);
        if (color is 5 or 7)
            throw new InvalidDataException("The JPEG XR picture uses a reserved colour format.");
        Color = (JpegXrColor)color;
        if (_alpha && Color != JpegXrColor.YOnly)
            throw new InvalidDataException("The JPEG XR picture's alpha plane isn't a single component.");
        Scaled = bits.Flag();
        Bands = bits.Int(4);
        if (Bands > 3)
            throw new InvalidDataException("The JPEG XR picture uses reserved bands.");
        if (Yuv)
        {
            if (Subsampled)
            {
                bits.Bits(1);
                ChromaCenteringX = bits.Int(3);
            }
            else
            {
                bits.Bits(4);
            }
            if (Color == JpegXrColor.Yuv420)
            {
                bits.Bits(1);
                ChromaCenteringY = bits.Int(3);
            }
            else
            {
                bits.Bits(4);
            }
        }
        else if (Color == JpegXrColor.NComponent)
        {
            var minus1 = bits.Int(4);
            if (minus1 == 0xF)
                Components = bits.Int(12) + 16;
            else
            {
                Components = minus1 + 1;
                bits.Bits(4);
            }
        }
        if (Components == 0)
        {
            Components = Color switch
            {
                JpegXrColor.YOnly => 1,
                JpegXrColor.Yuvk => 4,
                _ => 3,
            };
        }
        if (Components > MaxComponents)
            throw new NotSupportedException($"JPEG XR pictures of {Components} colour components are not read.");
        if (_image.BitDepth is JpegXrHeader.Bd16 or JpegXrHeader.Bd16S or JpegXrHeader.Bd32S)
            ShiftBits = bits.Int(8);
        if (_image.BitDepth == JpegXrHeader.Bd32F)
        {
            LenMantissa = bits.Int(8);
            ExpBias = (sbyte)bits.Int(8);
        }
        _dcUniform = bits.Flag();
        if (_dcUniform)
            ReadDcQp(bits, _planeDc);
        if (Bands != 3)
        {
            bits.Bits(1);
            _lpUniform = bits.Flag();
            if (_lpUniform)
            {
                _planeLpCount = 1;
                ReadQps(bits, _planeLp, 1);
            }
            if (Bands != 2)
            {
                bits.Bits(1);
                _hpUniform = bits.Flag();
                if (_hpUniform)
                {
                    _planeHpCount = 1;
                    ReadQps(bits, _planeHp, 1);
                }
            }
        }
        bits.AlignToByte();
    }

    /// <summary>Colour components read (CMYK with alpha has five; more are left out).</summary>
    public const int MaxComponents = 8;

    // ───────────────────────── Quantization parameters (8.4.22–8.4.24, 9.7) ─────────────────────────

    // Plane-level QP sets, and each tile's (QuantParam[component][index]); a frequency mode codestream reads a tile's
    // bands far apart, and a band's QPs may be the previous band's of the same tile.
    private readonly int[] _planeDc = new int[MaxComponents];
    private readonly int[][] _planeLp = NewQps();
    private readonly int[][] _planeHp = NewQps();
    private int _planeLpCount = 1, _planeHpCount = 1;
    private TileQps[] _tiles = [];
    private TileQps _tile = new();

    private sealed class TileQps
    {
        public readonly int[] Dc = new int[MaxComponents];
        public readonly int[][] Lp = NewQps();
        public readonly int[][] Hp = NewQps();
        public int LpCount = 1, HpCount = 1;
        public bool UseDcQp, UseLpQp;
    }

    private static int[][] NewQps() => [.. Enumerable.Range(0, MaxComponents).Select(_ => new int[16])];

    /// <summary>DC_QP( ) into <paramref name="qp"/>[component].</summary>
    private void ReadDcQp(JpegXrBits bits, int[] qp)
    {
        var mode = Components != 1 ? bits.Int(2) : 0;
        ReadSet(bits, mode, (component, value) => qp[component] = value);
    }

    /// <summary>LP_QP( ) / HP_QP( ) into <paramref name="qp"/>[component][index].</summary>
    private void ReadQps(JpegXrBits bits, int[][] qp, int count)
    {
        for (var q = 0; q < count; q++)
        {
            var mode = Components != 1 ? bits.Int(2) : 0;
            ReadSet(bits, mode, (component, value) => qp[component][q] = value);
        }
    }

    private void ReadSet(JpegXrBits bits, int mode, Action<int, int> set)
    {
        switch (mode)
        {
            case 0:
                {
                    var all = bits.Int(8);
                    for (var i = 0; i < Components; i++)
                        set(i, all);
                    break;
                }
            case 1:
                {
                    set(0, bits.Int(8));
                    var chroma = bits.Int(8);
                    for (var i = 1; i < Components; i++)
                        set(i, chroma);
                    break;
                }
            case 2:
                for (var i = 0; i < Components; i++)
                    set(i, bits.Int(8));
                break;
            default:
                throw new InvalidDataException("The JPEG XR picture uses a reserved quantizer mode.");
        }
    }

    /// <summary>Makes tile <paramref name="tile"/> the one whose syntax is read (its QPs are kept).</summary>
    public void BeginTile(int tile)
    {
        if (_tiles.Length == 0)
            _tiles = new TileQps[_image.TileCount];
        _tile = _tiles[tile] ??= new TileQps();
        TileIndex = tile;
    }

    /// <summary>The tile being read.</summary>
    public int TileIndex { get; private set; }

    /// <summary>TILE_HEADER_DC( ) and the tile-level DC QPs (9.7.1).</summary>
    public void TileHeaderDc(JpegXrBits bits)
    {
        if (_dcUniform)
            Array.Copy(_planeDc, _tile.Dc, MaxComponents);
        else
            ReadDcQp(bits, _tile.Dc);
        // Bands the codestream lacks still need QPs of their own.
        TileHeaderLowpass(null);
    }

    /// <summary>TILE_HEADER_LOWPASS( ) and the tile-level LP QPs (9.7.2); null bits: the band is absent.</summary>
    public void TileHeaderLowpass(JpegXrBits? bits)
    {
        _tile.UseDcQp = false;
        if (bits is null || Bands == 3)
        {
            _tile.LpCount = 1;
            for (var i = 0; i < Components; i++)
                _tile.Lp[i][0] = _tile.Dc[i];
        }
        else if (_lpUniform)
        {
            _tile.LpCount = _planeLpCount;
            CopyQps(_planeLp, _tile.Lp);
        }
        else if (_tile.UseDcQp = bits.Flag())
        {
            _tile.LpCount = 1;
            for (var i = 0; i < Components; i++)
                _tile.Lp[i][0] = _tile.Dc[i];
        }
        else
        {
            _tile.LpCount = bits.Int(4) + 1;
            ReadQps(bits, _tile.Lp, _tile.LpCount);
        }
        TileHeaderHighpass(null);
    }

    /// <summary>TILE_HEADER_HIGHPASS( ) and the tile-level HP QPs (9.7.3); null bits: the band is absent.</summary>
    public void TileHeaderHighpass(JpegXrBits? bits)
    {
        _tile.UseLpQp = false;
        if (bits is null || Bands >= 2)
        {
            _tile.HpCount = _tile.LpCount;
            CopyQps(_tile.Lp, _tile.Hp);
        }
        else if (_hpUniform)
        {
            _tile.HpCount = _planeHpCount;
            CopyQps(_planeHp, _tile.Hp);
        }
        else if (_tile.UseLpQp = bits.Flag())
        {
            _tile.HpCount = _tile.LpCount;
            CopyQps(_tile.Lp, _tile.Hp);
        }
        else
        {
            _tile.HpCount = bits.Int(4) + 1;
            ReadQps(bits, _tile.Hp, _tile.HpCount);
        }
    }

    private static void CopyQps(int[][] from, int[][] to)
    {
        for (var i = 0; i < from.Length; i++)
            Array.Copy(from[i], to[i], 16);
    }

    /// <summary>DECODE_QP_INDEX( ) (Table 47).</summary>
    private static int QpIndex(JpegXrBits bits, int count)
    {
        ReadOnlySpan<int> width = [0, 0, 1, 1, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4];
        if (!bits.Flag())
            return 0;
        var index = bits.Int(width[count]) + 1;
        if (index >= count)
            throw new InvalidDataException("The JPEG XR picture names a quantizer it doesn't have.");
        return index;
    }

    /// <summary>LP_QP_INDEX / HP_QP_INDEX of a macroblock, where the tile syntax reads them.</summary>
    public void ReadQpIndices(JpegXrBits bits, int mb, bool lowpass, bool highpass)
    {
        if (lowpass && Bands != 3)
            _lpIndex[mb] = (byte)(_tile.LpCount > 1 && !_tile.UseDcQp ? QpIndex(bits, _tile.LpCount) : 0);
        if (highpass && Bands is 0 or 1)
        {
            _hpIndex[mb] = (byte)(_tile.HpCount > 1 && !_tile.UseLpQp
                ? QpIndex(bits, _tile.HpCount)
                : _tile.UseLpQp ? _lpIndex[mb] : 0);
        }
    }

    /// <summary>QuantMap( ) (Table 149).</summary>
    private int QuantMap(int qp, int scaledShift)
    {
        if (qp == 0)
            return 1;
        int man, exp;
        if (!Scaled)
        {
            if (qp < 32)
                (man, exp) = ((qp + 3) >> 2, 0);
            else if (qp < 48)
                (man, exp) = ((16 + qp % 16 + 1) >> 1, (qp >> 4) - 2);
            else
                (man, exp) = (16 + qp % 16, (qp >> 4) - 1 - 2);
        }
        else if (qp < 16)
        {
            (man, exp) = (qp, scaledShift);
        }
        else
        {
            (man, exp) = (16 + qp % 16, (qp >> 4) - 1 + scaledShift);
        }
        return man << exp;
    }
}
