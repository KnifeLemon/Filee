// The part of a JPEG XR codestream after the plane headers (ITU-T T.832 8.2, 8.5, 8.7): the tile index table, the
// subsequent bytes (profile and level), and the tile packets, read in spatial mode (one packet per tile, macroblock
// after macroblock) or frequency mode (DC, lowpass, highpass and flexbits packets of every tile in turn). The alpha
// plane's macroblocks follow the primary plane's in the same packets.

namespace Filee.Engines.JpegXr;

internal sealed class JpegXrTiles(JpegXrHeader header, JpegXrPlane primary, JpegXrPlane? alpha, JpegXrBits bits)
{
    private int _trim;

    public void Read()
    {
        var tiles = header.TileCount;
        var bands = primary.NumBands;
        var entries = header.FrequencyMode ? tiles * bands : tiles;
        var offsets = new long[entries];
        if (header.IndexTable)
        {
            if (bits.Int(16) != 1)
                throw new InvalidDataException("The JPEG XR codestream's tile index is damaged.");
            for (var n = 0; n < entries; n++)
                offsets[n] = Escaped();
        }
        else if (entries > 1)
        {
            throw new InvalidDataException("The JPEG XR codestream has several tile packets but no index of them.");
        }
        var subsequent = Escaped();
        var tileData = (long)bits.BytePosition + subsequent;
        if (tileData > int.MaxValue)
            throw JpegXrBits.Damaged();
        primary.Allocate();
        alpha?.Allocate();
        if (!header.FrequencyMode)
        {
            for (var n = 0; n < tiles; n++)
            {
                Seek(tileData, offsets[n]);
                Spatial(n);
            }
            return;
        }
        for (var band = 0; band < bands; band++)
        {
            for (var n = 0; n < tiles; n++)
            {
                Seek(tileData, offsets[n * bands + band]);
                Frequency(n, band);
            }
        }
    }

    /// <summary>VLW_ESC( ) (Table 19).</summary>
    private long Escaped()
    {
        var first = bits.Int(8);
        if (first < 0xFB)
            return first * 256 + bits.Int(8);
        if (first == 0xFB)
            return bits.Bits(32);
        if (first == 0xFC)
        {
            var high = (long)bits.Bits(32);
            var low = bits.Bits(32);
            return high > 0 ? long.MaxValue : low;
        }
        return 0;
    }

    private void Seek(long tileData, long offset)
    {
        if (offset < 0 || tileData + offset > int.MaxValue)
            throw JpegXrBits.Damaged();
        bits.Seek((int)(tileData + offset));
    }

    /// <summary>TILE_STARTCODE and ARBITRARY_BYTE.</summary>
    private void StartCode()
    {
        if (bits.Bits(24) != 1)
            throw new InvalidDataException("The JPEG XR codestream's tile packet is damaged.");
        bits.Bits(8);
    }

    private IEnumerable<JpegXrPlane> Planes()
    {
        yield return primary;
        if (alpha is not null)
            yield return alpha;
    }

    /// <summary>The macroblocks of tile <paramref name="n"/> in raster order; each is made the planes' current one.</summary>
    private IEnumerable<int> Macroblocks(int n)
    {
        var tx = n % header.TileColumns;
        var ty = n / header.TileColumns;
        var (left, right, top) = (header.TileLeft[tx], header.TileLeft[tx + 1], header.TileTop[ty]);
        for (var mby = top; mby < header.TileTop[ty + 1]; mby++)
        {
            for (var mbx = left; mbx < right; mbx++)
            {
                primary.At(mbx, mby, left, right, top);
                alpha?.At(mbx, mby, left, right, top);
                yield return mby * header.MbWidth + mbx;
            }
        }
    }

    /// <summary>TILE_SPATIAL( ) (Table 39).</summary>
    private void Spatial(int n)
    {
        StartCode();
        _trim = header.TrimFlexbits ? bits.Int(4) : 0;
        foreach (var plane in Planes())
        {
            plane.BeginTile(n);
            plane.TileHeaderDc(bits);
            plane.TileHeaderLowpass(bits);
            plane.TileHeaderHighpass(bits);
        }
        foreach (var mb in Macroblocks(n))
        {
            foreach (var plane in Planes())
            {
                plane.ReadQpIndices(bits, mb, lowpass: true, highpass: true);
                plane.MbDc(bits);
                if (plane.Bands != 3)
                    plane.MbLp(bits);
                plane.DecodeDcLp();
                if (plane.Bands is 0 or 1)
                {
                    plane.MbCbphp(bits);
                    plane.MbHp(bits, flex: true, _trim);
                    plane.DecodeHp();
                }
            }
        }
    }

    /// <summary>TILE_DC( ), TILE_LOWPASS( ), TILE_HIGHPASS( ) or TILE_FLEXBITS( ) (Tables 40–46).</summary>
    private void Frequency(int n, int band)
    {
        StartCode();
        if (band == 3)
            _trim = header.TrimFlexbits ? bits.Int(4) : 0;
        foreach (var plane in Planes())
        {
            plane.BeginTile(n);
            switch (band)
            {
                case 0:
                    plane.TileHeaderDc(bits);
                    break;
                case 1:
                    plane.TileHeaderLowpass(bits);
                    break;
                case 2:
                    plane.TileHeaderHighpass(bits);
                    break;
            }
        }
        foreach (var mb in Macroblocks(n))
        {
            foreach (var plane in Planes())
            {
                switch (band)
                {
                    case 0:
                        plane.MbDc(bits);
                        if (plane.Bands == 3)
                            plane.DecodeDcLp();
                        break;
                    case 1 when plane.Bands != 3:
                        plane.ReadQpIndices(bits, mb, lowpass: true, highpass: false);
                        plane.MbLp(bits);
                        plane.DecodeDcLp();
                        break;
                    case 2 when plane.Bands is 0 or 1:
                        plane.ReadQpIndices(bits, mb, lowpass: false, highpass: true);
                        plane.MbCbphp(bits);
                        plane.MbHp(bits, flex: false, 0);
                        if (plane.Bands == 1)
                            plane.DecodeHp();
                        break;
                    case 3 when plane.Bands == 0:
                        plane.MbFlexbits(bits, _trim);
                        plane.DecodeHp();
                        break;
                }
            }
        }
    }
}
