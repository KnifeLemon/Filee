// The code tables and constant tables of ITU-T T.832 that Filee's JPEG XR decoder parses with (clause 8), numbered
// as the spec numbers them.

namespace Filee.Engines.JpegXr;

internal static class JpegXrTables
{
    /// <summary>Table 51: VAL_DC_YUV.</summary>
    public static readonly JpegXrVlc ValDcYuv = new("10", "001", "00001", "0001", "11", "010", "00000", "011");

    /// <summary>Table 52: ABS_LEVEL_INDEX, code tables 0 and 1.</summary>
    public static readonly JpegXrVlc[] AbsLevelIndex =
    [
        new("01", "10", "11", "001", "0001", "00000", "00001"),
        new("1", "01", "001", "0001", "00001", "000000", "000001"),
    ];

    /// <summary>Table 55: CBPLP_YUV1 for YUV 4:4:4.</summary>
    public static readonly JpegXrVlc CbplpYuv444 = new("0", "100", "1010", "1011", "1100", "1101", "1110", "1111");

    /// <summary>Table 56: CBPLP_YUV1 for YUV 4:2:0 and 4:2:2.</summary>
    public static readonly JpegXrVlc CbplpYuv42x = new("0", "10", "110", "111");

    /// <summary>Table 59 (and 60): NUM_CBPHP; NUM_BLKCBPHP when the format has no joint chroma.</summary>
    public static readonly JpegXrVlc[] NumCbphp =
    [
        new("1", "01", "001", "0000", "0001"),
        new("1", "000", "001", "010", "011"),
    ];

    /// <summary>Table 61: NUM_BLKCBPHP for YUV formats.</summary>
    public static readonly JpegXrVlc[] NumBlkCbphpYuv =
    [
        new("010", "00000", "0010", "00001", "00010", "1", "011", "00011", "0011"),
        new("1", "001", "010", "0001", "000001", "011", "00001", "0000000", "0000001"),
    ];

    /// <summary>Table 62: CHR_CBPHP, VAL_INC and CBPHP_CH_BLK.</summary>
    public static readonly JpegXrVlc ChrCbphp = new("1", "01", "00");

    /// <summary>Table 63: NUM_CH_BLK.</summary>
    public static readonly JpegXrVlc NumChBlk = new("1", "01", "000", "001");

    /// <summary>Table 64: REF_CBPHP1.</summary>
    public static readonly JpegXrVlc RefCbphp1 = JpegXrVlc.Mapped(("00", 3), ("01", 5), ("100", 6), ("101", 9), ("110", 10), ("111", 12));

    /// <summary>Tables 76, 77 and 78: RUN_VALUE when the longest run is 2, 3 or 4 (index = longest run).</summary>
    public static readonly JpegXrVlc?[] RunValue =
    [
        null,
        null,
        JpegXrVlc.Mapped(("1", 1), ("0", 2)),
        JpegXrVlc.Mapped(("1", 1), ("01", 2), ("00", 3)),
        JpegXrVlc.Mapped(("1", 1), ("01", 2), ("001", 3), ("000", 4)),
    ];

    /// <summary>Table 79: RUN_INDEX.</summary>
    public static readonly JpegXrVlc RunIndex = new("1", "01", "001", "0000", "0001");

    /// <summary>Table 80: INDEX_A, code tables 0 to 3.</summary>
    public static readonly JpegXrVlc[] IndexA =
    [
        new("1", "00000", "001", "00001", "01", "0001"),
        new("01", "0000", "10", "0001", "11", "001"),
        new("0000", "0001", "01", "10", "11", "001"),
        new("00000", "00001", "01", "1", "0001", "001"),
    ];

    /// <summary>Table 81: INDEX_B.</summary>
    public static readonly JpegXrVlc IndexB = JpegXrVlc.Mapped(("0", 0), ("10", 2), ("110", 1), ("111", 3));

    /// <summary>Table 82: FIRST_INDEX, code tables 0 to 4.</summary>
    public static readonly JpegXrVlc[] FirstIndex =
    [
        new("00001", "000001", "0000000", "0000001", "00100", "010", "00101", "1", "00110", "0001", "00111", "011"),
        new("0010", "00010", "000000", "000001", "0011", "010", "00011", "11", "011", "100", "00001", "101"),
        new("11", "001", "0000000", "0000001", "00001", "010", "0000010", "011", "100", "101", "0000011", "0001"),
        new("001", "11", "0000000", "00001", "00010", "010", "0000001", "011", "00011", "100", "000001", "101"),
        new("010", "1", "0000001", "0001", "0000010", "011", "00000000", "0010", "0000011", "0011", "00000001", "00001"),
    ];

    /// <summary>Table 86: AbslevelIndexDelta[0].</summary>
    public static readonly int[] AbsLevelIndexDelta = [1, 0, -1, -1, -1, -1, -1];

    /// <summary>Table 87: FirstIndexDelta[m][n].</summary>
    public static readonly int[][] FirstIndexDelta =
    [
        [1, 1, 1, 1, 1, 0, 0, -1, 2, 1, 0, 0],
        [2, 2, -1, -1, -1, 0, -2, -1, 0, 0, -2, -1],
        [-1, 1, 0, 2, 0, 0, 0, 0, -2, 0, 1, 1],
        [0, 1, 0, 1, -2, 0, -1, -1, -2, -1, -2, -2],
    ];

    /// <summary>Table 88: Index1Delta[m][n].</summary>
    public static readonly int[][] Index1Delta =
    [
        [-1, 1, 1, 1, 0, 1],
        [-2, 0, 0, 2, 0, 0],
        [-1, -1, 0, 1, -2, 0],
    ];

    /// <summary>Tables 89 and 90: NumCBPHPDelta, NumBlkCBPHPDelta without joint chroma.</summary>
    public static readonly int[] NumCbphpDelta = [0, -1, 0, 1, 1];

    /// <summary>Table 91: NumBlkCBPHPDelta for YUV formats.</summary>
    public static readonly int[] NumBlkCbphpYuvDelta = [2, 2, 1, 1, -1, -2, -2, -2, -3];

    /// <summary>Table 107: ScanOrder0 and ScanOrder1 (index 0 unused).</summary>
    public static readonly int[] ScanOrder0 = [0, 4, 1, 5, 8, 2, 9, 6, 12, 3, 10, 13, 7, 14, 11, 15];

    public static readonly int[] ScanOrder1 = [0, 1, 2, 5, 4, 3, 6, 9, 8, 7, 12, 15, 13, 10, 11, 14];

    /// <summary>Table 108: ScanTotals (index 0 unused).</summary>
    public static readonly int[] ScanTotals = [0, 32, 30, 28, 26, 24, 22, 20, 18, 16, 14, 12, 10, 8, 6, 4];

    /// <summary>iTranspose444 of Tables 53 and 84.</summary>
    public static readonly int[] Transpose444 = [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];

    public static readonly int[] Transpose422 = [0, 2, 1, 3, 4, 6, 5, 7];

    public static readonly int[] Transpose420 = [0, 2, 1, 3];

    /// <summary>iHierScanOrder of Tables 69, 70 and 83.</summary>
    public static readonly int[] HierScanOrder = [0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15];

    /// <summary>Table 164: InvPermArr.</summary>
    public static readonly int[] InvPermArr = [0, 8, 4, 13, 2, 15, 3, 14, 1, 12, 5, 9, 7, 11, 6, 10];
}
