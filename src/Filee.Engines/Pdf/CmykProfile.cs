// The CMYK ICC profile of the DefaultCMYK colour space PdfAConformance gives a PDF/A that draws in DeviceCMYK without
// a CMYK output intent (ISO 19005-2 6.2.4.3 allows DeviceCMYK under a device-independent DefaultCMYK). Built here in
// code, like SrgbProfile, so nobody's profile is copied and there is no licence to carry.
//
// It describes the plain conversion PDF viewers use for device CMYK when they have no profile of their own
// (pdf.js, PDFium): red = (1 − C)(1 − K), green = (1 − M)(1 − K), blue = (1 − Y)(1 − K) in sRGB, taken on to CIE Lab
// (D50) through sRGB's curve and primaries. So the PDF/A looks as the PDF did in those viewers; it is not a press
// profile, and a PDF that brings its own CMYK intent keeps that one instead. Form: an ICC version 2.1 input profile
// (ICC.1:2001-04, class scnr, CMYK data, Lab connection space) with the tags such a profile needs: desc, cprt, wtpt and
// an A2B0 lut16Type table of 7 grid points per channel (2401 points, about 15 KB before the PDF deflates it), identity
// input and output curves.

using System.Buffers.Binary;

namespace Filee.Engines.Pdf;

/// <summary>A CMYK ICC profile made in code.</summary>
internal static class CmykProfile
{
    /// <summary>The profile's description.</summary>
    public const string Name = "Filee CMYK (uncalibrated device CMYK)";

    /// <summary>Grid points per channel of the colour lookup table.</summary>
    internal const int GridPoints = 7;

    private static readonly Lazy<byte[]> Profile = new(Build);

    /// <summary>The profile's bytes (a copy: callers may keep or change it).</summary>
    public static byte[] Bytes => (byte[])Profile.Value.Clone();

    /// <summary>The sRGB colour the profile gives a CMYK colour (each 0–1).</summary>
    internal static (double R, double G, double B) ToRgb(double c, double m, double y, double k) =>
        ((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));

    /// <summary>CIE Lab (D50) of an sRGB colour.</summary>
    internal static (double L, double A, double B) ToLab(double r, double g, double b)
    {
        var columns = Primaries.Value;
        double[] linear = [SrgbProfile.ToLinear(r), SrgbProfile.ToLinear(g), SrgbProfile.ToLinear(b)];
        var xyz = new double[3];
        for (var i = 0; i < 3; i++)
            xyz[i] = columns[0][i] * linear[0] + columns[1][i] * linear[1] + columns[2][i] * linear[2];
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        var (fx, fy, fz) = (F(xyz[0] / SrgbProfile.D50[0]), F(xyz[1] / SrgbProfile.D50[1]), F(xyz[2] / SrgbProfile.D50[2]));
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static readonly Lazy<double[][]> Primaries = new(SrgbProfile.AdaptedPrimaries);

    private static byte[] Build()
    {
        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", SrgbProfile.Description(Name)),
            ("cprt", SrgbProfile.Text("No copyright, use freely")),
            ("wtpt", SrgbProfile.Xyz(SrgbProfile.D50)),
            ("A2B0", Lut()),
        };
        var tableEnd = 128 + 4 + 12 * tags.Count;
        using var data = new MemoryStream();
        var entries = new List<(string Signature, int Offset, int Size)>();
        foreach (var (signature, bytes) in tags)
        {
            entries.Add((signature, tableEnd + (int)data.Length, bytes.Length));
            SrgbProfile.Append(data, bytes);
        }

        var profile = new byte[tableEnd + data.Length];
        var span = profile.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0x02100000); // version 2.1.0
        SrgbProfile.Ascii(span[12..], "scnr");
        SrgbProfile.Ascii(span[16..], "CMYK");
        SrgbProfile.Ascii(span[20..], "Lab ");
        ushort[] date = [2026, 11, 21, 0, 0, 0];
        for (var i = 0; i < date.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(span[(24 + 2 * i)..], date[i]);
        SrgbProfile.Ascii(span[36..], "acsp");
        for (var i = 0; i < 3; i++)
            BinaryPrimitives.WriteInt32BigEndian(span[(68 + 4 * i)..], SrgbProfile.Fixed(SrgbProfile.D50[i]));
        BinaryPrimitives.WriteUInt32BigEndian(span[128..], (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = span[(132 + 12 * i)..];
            SrgbProfile.Ascii(entry, entries[i].Signature);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)entries[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)entries[i].Size);
        }
        data.ToArray().CopyTo(profile, tableEnd);
        return profile;
    }

    /// <summary>lut16Type (ICC.1:2001-04 6.5.7): CMYK in, Lab out in the version 2 16-bit encoding.</summary>
    private static byte[] Lut()
    {
        const int inputs = 4, outputs = 3, tableEntries = 2;
        var points = (int)Math.Pow(GridPoints, inputs);
        var bytes = new byte[52 + 2 * inputs * tableEntries + 2 * points * outputs + 2 * outputs * tableEntries];
        var span = bytes.AsSpan();
        SrgbProfile.Ascii(span, "mft2");
        span[8] = inputs;
        span[9] = outputs;
        span[10] = GridPoints;
        for (var i = 0; i < 3; i++)
            BinaryPrimitives.WriteInt32BigEndian(span[(12 + 16 * i)..], 0x10000); // identity matrix (used only for XYZ input)
        BinaryPrimitives.WriteUInt16BigEndian(span[48..], tableEntries);
        BinaryPrimitives.WriteUInt16BigEndian(span[50..], tableEntries);
        var at = 52;
        for (var i = 0; i < inputs; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[at..], 0);
            BinaryPrimitives.WriteUInt16BigEndian(span[(at + 2)..], ushort.MaxValue);
            at += 4;
        }
        // The first input channel (cyan) varies slowest.
        double Step(int index) => index / (double)(GridPoints - 1);
        for (var c = 0; c < GridPoints; c++)
        {
            for (var m = 0; m < GridPoints; m++)
            {
                for (var y = 0; y < GridPoints; y++)
                {
                    for (var k = 0; k < GridPoints; k++)
                    {
                        var (r, g, b) = ToRgb(Step(c), Step(m), Step(y), Step(k));
                        var lab = ToLab(r, g, b);
                        BinaryPrimitives.WriteUInt16BigEndian(span[at..], Encode(lab.L / 100 * 0xFF00));
                        BinaryPrimitives.WriteUInt16BigEndian(span[(at + 2)..], Encode((lab.A + 128) * 256));
                        BinaryPrimitives.WriteUInt16BigEndian(span[(at + 4)..], Encode((lab.B + 128) * 256));
                        at += 6;
                    }
                }
            }
        }
        for (var i = 0; i < outputs; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[at..], 0);
            BinaryPrimitives.WriteUInt16BigEndian(span[(at + 2)..], ushort.MaxValue);
            at += 4;
        }
        return bytes;

        static ushort Encode(double value) => (ushort)Math.Clamp(Math.Round(value), 0, ushort.MaxValue);
    }
}
