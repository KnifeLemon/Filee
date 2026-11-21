// IMAGE_HEADER( ) of a JPEG XR codestream (ITU-T T.832 8.3) and the image partition it defines (6.4): extended size,
// macroblocks, tiles and windowing.

namespace Filee.Engines.JpegXr;

internal sealed class JpegXrHeader
{
    public const int Bd1White = 0, Bd8 = 1, Bd16 = 2, Bd16S = 3, Bd16F = 4, Bd32S = 6, Bd32F = 7, Bd5 = 8, Bd10 = 9, Bd565 = 10, Bd1Black = 15;
    public const int YOnly = 0, Yuv420 = 1, Yuv422 = 2, Yuv444 = 3, Cmyk = 4, CmykDirect = 5, NComponent = 6, Rgb = 7, Rgbe = 8;

    public bool HardTiling { get; private set; }

    public bool FrequencyMode { get; private set; }

    public int Orientation { get; private set; }

    public bool IndexTable { get; private set; }

    public int Overlap { get; private set; }

    public bool TrimFlexbits { get; private set; }

    public bool RedBlueNotSwapped { get; private set; }

    public bool Premultiplied { get; private set; }

    public bool AlphaPlane { get; private set; }

    public int OutputColor { get; private set; }

    public int BitDepth { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Top { get; private set; }

    public int Left { get; private set; }

    public int ExtendedWidth { get; private set; }

    public int ExtendedHeight { get; private set; }

    public int MbWidth => ExtendedWidth / 16;

    public int MbHeight => ExtendedHeight / 16;

    public int TileColumns { get; private set; } = 1;

    public int TileRows { get; private set; } = 1;

    public int TileCount => TileColumns * TileRows;

    /// <summary>LeftMBIndexOfTile[0…TileColumns] and TopMBIndexOfTile[0…TileRows].</summary>
    public int[] TileLeft { get; private set; } = [];

    public int[] TileTop { get; private set; } = [];

    /// <summary>Tiles a codestream may have before it is taken as damage (the spec allows 4096 × 4096).</summary>
    public const int MaxTiles = 65_536;

    /// <param name="maxPixels">Largest extended image (in luma samples) the decoder makes room for.</param>
    public static JpegXrHeader Read(JpegXrBits bits, long maxPixels)
    {
        if (bits.Bits(32) != 0x574D5048 || bits.Bits(32) != 0x4F544F00)
            throw new InvalidDataException("The JPEG XR codestream has no WMPHOTO signature.");
        var header = new JpegXrHeader();
        var reserved = bits.Int(4);
        if (reserved > 1)
            throw new NotSupportedException("The JPEG XR codestream is of a later version.");
        header.HardTiling = bits.Flag();
        bits.Bits(3);
        var tiling = bits.Flag();
        header.FrequencyMode = bits.Flag();
        header.Orientation = bits.Int(3);
        header.IndexTable = bits.Flag();
        header.Overlap = bits.Int(2);
        if (header.Overlap == 3)
            throw new InvalidDataException("The JPEG XR codestream uses a reserved overlap mode.");
        var shortHeader = bits.Flag();
        bits.Bits(1); // LONG_WORD_FLAG: 32-bit arithmetic is used either way.
        var windowing = bits.Flag();
        header.TrimFlexbits = bits.Flag();
        bits.Bits(1);
        header.RedBlueNotSwapped = bits.Flag();
        header.Premultiplied = bits.Flag();
        header.AlphaPlane = bits.Flag();
        header.OutputColor = bits.Int(4);
        header.BitDepth = bits.Int(4);
        if (header.OutputColor > Rgbe || header.BitDepth is 5 or (> 10 and < 15))
            throw new InvalidDataException("The JPEG XR codestream uses a reserved output format.");
        long width = shortHeader ? bits.Bits(16) : bits.Bits(32);
        long height = shortHeader ? bits.Bits(16) : bits.Bits(32);
        width++;
        height++;
        int verticalTiles = 0, horizontalTiles = 0;
        if (tiling)
        {
            verticalTiles = bits.Int(12);
            horizontalTiles = bits.Int(12);
        }
        var tileWidths = new int[verticalTiles];
        var tileHeights = new int[horizontalTiles];
        for (var n = 0; n < verticalTiles; n++)
            tileWidths[n] = bits.Int(shortHeader ? 8 : 16);
        for (var n = 0; n < horizontalTiles; n++)
            tileHeights[n] = bits.Int(shortHeader ? 8 : 16);
        int top = 0, left = 0, bottom, right;
        if (windowing)
        {
            top = bits.Int(6);
            left = bits.Int(6);
            bottom = bits.Int(6);
            right = bits.Int(6);
        }
        else
        {
            bottom = height % 16 == 0 ? 0 : (int)(16 - height % 16);
            right = width % 16 == 0 ? 0 : (int)(16 - width % 16);
        }
        var extendedWidth = width + left + right;
        var extendedHeight = height + top + bottom;
        if (extendedWidth % 16 != 0 || extendedHeight % 16 != 0)
            throw new InvalidDataException("The JPEG XR codestream's size is no whole number of macroblocks.");
        if (extendedWidth * extendedHeight > maxPixels || extendedWidth > int.MaxValue / 16 || extendedHeight > int.MaxValue / 16)
            throw JpegXrHeader.TooLarge();
        (header.Width, header.Height, header.Top, header.Left) = ((int)width, (int)height, top, left);
        (header.ExtendedWidth, header.ExtendedHeight) = ((int)extendedWidth, (int)extendedHeight);
        header.TileColumns = verticalTiles + 1;
        header.TileRows = horizontalTiles + 1;
        if ((long)header.TileColumns * header.TileRows > MaxTiles)
            throw new InvalidDataException("The JPEG XR codestream has more tiles than any picture needs.");
        header.TileLeft = Boundaries(tileWidths, header.MbWidth);
        header.TileTop = Boundaries(tileHeights, header.MbHeight);
        return header;
    }

    /// <summary>A picture larger than the decoder was allowed to make room for.</summary>
    public static InvalidDataException TooLarge() => new("The JPEG XR picture is larger than allowed.");

    /// <summary>DetermineLeftBoundaryofTile( ) / DetermineTopBoundaryofTile( ) (Tables 24, 25).</summary>
    private static int[] Boundaries(int[] sizes, int total)
    {
        var bounds = new int[sizes.Length + 2];
        for (var n = 0; n < sizes.Length; n++)
        {
            bounds[n + 1] = bounds[n] + sizes[n];
            if (sizes[n] == 0 || bounds[n + 1] >= total)
                throw new InvalidDataException("The JPEG XR codestream's tiles don't fit the picture.");
        }
        bounds[^1] = total;
        return bounds;
    }
}
