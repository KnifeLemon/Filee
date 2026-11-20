// The sRGB ICC profile of the PDF/A output intent Filee writes itself (PdfAConformance), built here from the numbers
// of IEC 61966-2-1 rather than shipped as a file: nobody's profile is copied, so there is no licence to carry. It is a
// version 2.1 display profile (ICC.1:2001-04) in the smallest form the format has: the sRGB primaries adapted from
// D65 to the D50 connection space with the Bradford transform, and one tone curve, the sRGB transfer function sampled
// at 1024 points, shared by the three channels. PDF/A-2 takes any ICC profile up to version 4 whose colour space
// matches the output intent's components (ISO 19005-2 6.2.3), so nothing more is needed.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Pdf;

/// <summary>An sRGB ICC profile made in code.</summary>
internal static class SrgbProfile
{
    /// <summary>The name the output intent and the profile's description give.</summary>
    public const string Name = "sRGB IEC61966-2.1";

    /// <summary>Points of the tone curve.</summary>
    internal const int CurvePoints = 1024;

    private static readonly Lazy<byte[]> Profile = new(Build);

    /// <summary>The profile's bytes (a copy: callers may keep or change it).</summary>
    public static byte[] Bytes => (byte[])Profile.Value.Clone();

    // D50, the profile connection space's white (ICC.1 7.2.16), and D65, sRGB's own (IEC 61966-2-1 table 1).
    private static readonly double[] D50 = [0.9642, 1.0, 0.8249];
    private static readonly (double X, double Y)[] Primaries = [(0.64, 0.33), (0.30, 0.60), (0.15, 0.06)];
    private static readonly (double X, double Y) D65 = (0.3127, 0.3290);

    private static byte[] Build()
    {
        var columns = AdaptedPrimaries();
        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", Description(Name)),
            ("cprt", Text("No copyright, use freely")),
            ("wtpt", Xyz(D50)),
            ("rXYZ", Xyz(columns[0])),
            ("gXYZ", Xyz(columns[1])),
            ("bXYZ", Xyz(columns[2])),
        };
        var curve = Curve();

        // Header (128 bytes), tag count, 12 bytes a tag, then the tag data, each on a 4-byte boundary.
        var tableEnd = 128 + 4 + 12 * (tags.Count + 3);
        using var data = new MemoryStream();
        var entries = new List<(string Signature, int Offset, int Size)>();
        foreach (var (signature, bytes) in tags)
        {
            entries.Add((signature, tableEnd + (int)data.Length, bytes.Length));
            Append(data, bytes);
        }
        var curveOffset = tableEnd + (int)data.Length;
        Append(data, curve);
        foreach (var signature in new[] { "rTRC", "gTRC", "bTRC" })
            entries.Add((signature, curveOffset, curve.Length)); // one curve for all three channels

        var profile = new byte[tableEnd + data.Length];
        var span = profile.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0x02100000); // version 2.1.0
        Ascii(span[12..], "mntr");
        Ascii(span[16..], "RGB ");
        Ascii(span[20..], "XYZ ");
        // A fixed date: the same profile on every run and every machine.
        ushort[] date = [2026, 11, 20, 0, 0, 0];
        for (var i = 0; i < date.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(span[(24 + 2 * i)..], date[i]);
        Ascii(span[36..], "acsp");
        // Platform, flags, maker, model, attributes and the perceptual intent stay zero.
        for (var i = 0; i < 3; i++)
            BinaryPrimitives.WriteInt32BigEndian(span[(68 + 4 * i)..], Fixed(D50[i]));
        BinaryPrimitives.WriteUInt32BigEndian(span[128..], (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = span[(132 + 12 * i)..];
            Ascii(entry, entries[i].Signature);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)entries[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)entries[i].Size);
        }
        data.ToArray().CopyTo(profile, tableEnd);
        return profile;
    }

    /// <summary>The sRGB transfer function (IEC 61966-2-1 5.2) from encoded value to linear light.</summary>
    internal static double ToLinear(double encoded) =>
        encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);

    /// <summary>The XYZ of the red, green and blue primaries at full strength, adapted to D50 (Bradford).</summary>
    internal static double[][] AdaptedPrimaries()
    {
        // RGB → XYZ for D65: the primaries' chromaticities, scaled so that full RGB is the white point.
        var xyz = Primaries.Select(p => new[] { p.X / p.Y, 1.0, (1 - p.X - p.Y) / p.Y }).ToArray();
        double[] white = [D65.X / D65.Y, 1.0, (1 - D65.X - D65.Y) / D65.Y];
        var scale = Solve([[xyz[0][0], xyz[1][0], xyz[2][0]], [xyz[0][1], xyz[1][1], xyz[2][1]], [xyz[0][2], xyz[1][2], xyz[2][2]]], white);
        var primaries = xyz.Select((p, i) => p.Select(v => v * scale[i]).ToArray()).ToArray();

        // Bradford: into cone responses, scale each from D65's to D50's, back to XYZ.
        double[][] bradford = [[0.8951, 0.2664, -0.1614], [-0.7502, 1.7135, 0.0367], [0.0389, -0.0685, 1.0296]];
        var from = Multiply(bradford, white);
        var to = Multiply(bradford, D50);
        return primaries.Select(p =>
        {
            var cone = Multiply(bradford, p);
            var adapted = cone.Select((c, i) => c * to[i] / from[i]).ToArray();
            return Solve(bradford, adapted);
        }).ToArray();
    }

    private static double[] Multiply(double[][] matrix, double[] vector) =>
        matrix.Select(row => row[0] * vector[0] + row[1] * vector[1] + row[2] * vector[2]).ToArray();

    /// <summary>Solves a 3 × 3 system by Cramer's rule.</summary>
    private static double[] Solve(double[][] m, double[] b)
    {
        static double Det(double[][] a) =>
            a[0][0] * (a[1][1] * a[2][2] - a[1][2] * a[2][1])
            - a[0][1] * (a[1][0] * a[2][2] - a[1][2] * a[2][0])
            + a[0][2] * (a[1][0] * a[2][1] - a[1][1] * a[2][0]);
        var det = Det(m);
        return Enumerable.Range(0, 3)
            .Select(column => Det(m.Select((row, r) => row.Select((v, c) => c == column ? b[r] : v).ToArray()).ToArray()) / det)
            .ToArray();
    }

    /// <summary>s15Fixed16Number (ICC.1 4.6).</summary>
    private static int Fixed(double value) => (int)Math.Round(value * 65536);

    private static byte[] Xyz(double[] value)
    {
        var bytes = new byte[20];
        Ascii(bytes, "XYZ ");
        for (var i = 0; i < 3; i++)
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8 + 4 * i), Fixed(value[i]));
        return bytes;
    }

    private static byte[] Curve()
    {
        var bytes = new byte[12 + 2 * CurvePoints];
        Ascii(bytes, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), CurvePoints);
        for (var i = 0; i < CurvePoints; i++)
        {
            var value = (ushort)Math.Round(ToLinear(i / (double)(CurvePoints - 1)) * ushort.MaxValue);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12 + 2 * i), value);
        }
        return bytes;
    }

    private static byte[] Text(string text)
    {
        var bytes = new byte[8 + text.Length + 1];
        Ascii(bytes, "text");
        Encoding.ASCII.GetBytes(text, bytes.AsSpan(8));
        return bytes;
    }

    /// <summary>textDescriptionType (ICC.1:2001-04 6.5.17): the ASCII name, no Unicode or ScriptCode names.</summary>
    private static byte[] Description(string text)
    {
        var bytes = new byte[12 + text.Length + 1 + 4 + 4 + 2 + 1 + 67];
        Ascii(bytes, "desc");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), (uint)(text.Length + 1));
        Encoding.ASCII.GetBytes(text, bytes.AsSpan(12));
        return bytes;
    }

    private static void Ascii(Span<byte> target, string text) => Encoding.ASCII.GetBytes(text, target);

    private static void Append(MemoryStream stream, byte[] bytes)
    {
        stream.Write(bytes);
        while (stream.Length % 4 != 0)
            stream.WriteByte(0);
    }
}
