// PDF shadings (PDF 32000 8.7.4.5) as PostScript LanguageLevel 3 shading dictionaries, for shfill and shading
// patterns. Function-based, axial and radial shadings and the mesh shadings (free-form and lattice triangles, Coons
// and tensor patches) keep their geometry; mesh data is decoded and written as a string (at most 64 KB). A shading in
// a device space keeps its functions (exponential and stitching ones as they are, the others sampled); in any other
// space its function and the colour conversion are sampled together into one function to a device space.

using PdfSharp.Pdf;

namespace Filee.Engines.Pdf.PostScript;

internal static class PsShadings
{
    private const int MaxMeshData = 65_535;

    /// <summary>Writes <c>&lt;&lt; … &gt;&gt;</c> for <paramref name="shading"/> (a dictionary or a mesh stream).</summary>
    public static void Write(PsWriter w, PdfDictionary shading, PdfDictionary? resources, bool withBackground)
    {
        var type = (int)PdfObjects.Number(shading, "/ShadingType", 0);
        if (type is < 1 or > 7)
            throw new InvalidDataException("A shading has an unknown type.");
        var space = PdfColorSpace.Read(PdfObjects.Get(shading, "/ColorSpace") ?? throw new InvalidDataException("A shading has no colour space."), resources);
        if (space is PdfColorSpace.PatternSpace or PdfColorSpace.Indexed)
            throw new PsUntranslatableException("A shading's colour space can't be drawn in PostScript.");
        var function = PdfObjects.Get(shading, "/Function") is { } f ? PdfFunction.Parse(f) : null;
        var device = space.Device;
        DeviceKind outputKind;
        if (device is { } kind)
            outputKind = kind;
        else if (function is not null)
            outputKind = space.ToDevice(space.Initial).Kind;
        else
            throw new PsUntranslatableException("A mesh shading without a function isn't in a device space.");

        w.Token("<<").Name("ShadingType").Number(type).Name("ColorSpace").Name(DeviceColor.SpaceName(outputKind));
        if (withBackground && PdfObjects.Numbers(shading, "/Background") is { } background && background.Length >= space.Components)
        {
            var color = space.ToDevice(background);
            w.Name("Background").Array(Enumerable.Range(0, color.Components).Select(i => color[i]).ToArray());
        }
        if (PdfObjects.Numbers(shading, "/BBox") is { Length: 4 } box)
            w.Name("BBox").Array(box);
        if (PdfObjects.Boolean(shading, "/AntiAlias", false))
            w.Name("AntiAlias").Token("true");

        var domain = PdfObjects.Numbers(shading, "/Domain");
        switch (type)
        {
            case 1:
                domain ??= [0, 1, 0, 1];
                w.Name("Domain").Array(domain);
                if (PdfObjects.Numbers(shading, "/Matrix") is { Length: 6 } matrix)
                    w.Name("Matrix").Array(matrix);
                break;
            case 2 or 3:
                {
                    var coords = PdfObjects.Numbers(shading, "/Coords");
                    if (coords is null || coords.Length != (type == 2 ? 4 : 6))
                        throw new InvalidDataException("A shading has no coordinates.");
                    domain ??= [0, 1];
                    w.Name("Coords").Array(coords).Name("Domain").Array(domain);
                    if (PdfObjects.Array(shading, "/Extend") is { Elements.Count: 2 } extend)
                    {
                        w.Name("Extend").Token("[")
                            .Token(PdfObjects.Resolve(extend.Elements[0]) is PdfBoolean { Value: true } ? "true" : "false")
                            .Token(PdfObjects.Resolve(extend.Elements[1]) is PdfBoolean { Value: true } ? "true" : "false").Token("]");
                    }
                    break;
                }
            default:
                Mesh(w, shading, type, space, function is not null);
                break;
        }

        if (function is not null)
        {
            w.Name("Function");
            if (device is not null)
            {
                function.Write(w);
            }
            else
            {
                var inputs = function.Inputs;
                var functionDomain = type == 1 ? domain! : type is 2 or 3 ? domain! : MeshDomain(shading);
                var outputs = DeviceColor.Black with { Kind = outputKind };
                PdfFunction.WriteSampled(w, inputs, outputs.Components, functionDomain[..(2 * inputs)], (x, y) =>
                {
                    Span<double> components = stackalloc double[Math.Max(function.Outputs, space.Components)];
                    function.Evaluate(x, components);
                    var color = space.ToDevice(components[..space.Components]);
                    if (color.Kind != outputKind)
                        color = color.ToRgb();
                    for (var i = 0; i < y.Length; i++)
                        y[i] = color[i];
                });
            }
        }
        w.Token(">>");
    }

    /// <summary>The parameter range of a mesh shading with a function: its Decode array's colour range.</summary>
    private static double[] MeshDomain(PdfDictionary shading) =>
        PdfObjects.Numbers(shading, "/Decode") is { Length: >= 6 } decode ? decode[^2..] : [0, 1];

    private static void Mesh(PsWriter w, PdfDictionary shading, int type, PdfColorSpace space, bool hasFunction)
    {
        if (shading.Stream is null)
            throw new InvalidDataException("A mesh shading has no data.");
        var decode = PdfObjects.Numbers(shading, "/Decode") ?? throw new InvalidDataException("A mesh shading has no Decode array.");
        var colorValues = hasFunction ? 1 : space.Components;
        if (decode.Length < 4 + 2 * colorValues)
            throw new InvalidDataException("A mesh shading's Decode array is short.");
        var data = PdfStreams.Decode(shading, MaxMeshData + 1);
        if (data.Length > MaxMeshData)
            throw new PsUntranslatableException("A mesh shading has too much data for PostScript.");
        w.Name("BitsPerCoordinate").Number(PdfObjects.Number(shading, "/BitsPerCoordinate", 8))
            .Name("BitsPerComponent").Number(PdfObjects.Number(shading, "/BitsPerComponent", 8));
        if (type != 5)
            w.Name("BitsPerFlag").Number(PdfObjects.Number(shading, "/BitsPerFlag", 8));
        else
            w.Name("VerticesPerRow").Number(PdfObjects.Number(shading, "/VerticesPerRow", 2));
        if (!hasFunction && space.Device is null)
            throw new PsUntranslatableException("A mesh shading isn't in a device space.");
        w.Name("Decode").Array(decode[..(4 + 2 * colorValues)]).Name("DataSource").Ascii85(data, literal: true);
    }
}
