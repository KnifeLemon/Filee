// Pictures → 3D relief models (GLB, glTF, STL, OBJ, 3MF, PLY): a closed solid whose top rises with the picture's
// brightness, on a flat base.

using System.Numerics;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Models3D;

/// <summary>
/// Turns a picture into a relief: one closed mesh with the picture's brightness as the height of its top (brighter is
/// higher, transparent is as low as black), four side walls and a flat base, written through
/// <see cref="ModelConverter.Write"/>.
/// </summary>
public sealed class ImageTo3DConverter : IConverter
{
    /// <summary>Length of the model's longest side (the picture's longer side), in millimetres.</summary>
    internal const float LongestSideMm = 100f;

    /// <summary>Thickness of the flat base under the relief, in millimetres.</summary>
    internal const float BaseMm = 2f;

    /// <summary>How much higher a white pixel stands than a black (or transparent) one, in millimetres.</summary>
    internal const float ReliefMm = 8f;

    /// <summary>Most grid points along a side: larger pictures are scaled down to this, one point per pixel.</summary>
    internal const int MaxGrid = 200;

    /// <summary>Light grey that transparent pixels are blended into (sRGB, 0–1).</summary>
    internal static readonly Vector3 Background = new(0.85f, 0.85f, 0.85f);

    /// <summary>Targets that keep a texture: the picture goes there as one; the others get vertex colours.</summary>
    private static readonly HashSet<string> Textured = ["glb", "gltf", "obj"];

    /// <summary>Every picture format the image engine reads (its sources in the image category; not EMF/WMF).</summary>
    public static readonly string[] InputFormats =
        [.. ImageEncoder.Sources.Where(f => FormatRegistry.FindById(f)?.Category == FormatCategory.Image)];

    public string Id => "image3d";
    public string DisplayName => "Pictures to 3D relief (built-in)";

    /// <summary>A model of a 200 × 200 grid is small, but the picture may be large while it is read.</summary>
    public int MaxParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>
    /// Only as the whole route (a picture straight to a model), so a picture never reaches a model through another
    /// format, and nothing else passes through here.
    /// </summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. from source in InputFormats
           from target in ModelConverter.Formats
           select new ConversionEdge(source, target, Cost: 25, DirectOnly: true)
    ];

    public EngineStatus GetStatus() =>
        EngineStatus.Available("Pictures → 3D relief (GLB, glTF, STL, OBJ, 3MF, PLY)", EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            progress?.Report(0.1);
            Model3D model;
            using (var images = ImageReader.Read(step.InputPath, step.From, step.Preset))
            {
                var picture = ImageEncoder.SelectFrames(images, step.From, "png").FirstOrDefault()
                    ?? throw new InvalidDataException("The file holds no picture.");
                progress?.Report(0.3);
                model = Build(picture, Textured.Contains(step.To), cancellationToken);
            }
            progress?.Report(0.7);

            var outputs = new List<string>();
            try
            {
                ModelConverter.Write(model, step.To, step.Output, step.Preset.Image, outputs, cancellationToken);
            }
            catch
            {
                foreach (var output in outputs)
                {
                    try { File.Delete(output); } catch { /* best effort */ }
                }
                throw;
            }

            progress?.Report(1.0);
            return (IReadOnlyList<string>)outputs;
        }, cancellationToken);

    /// <summary>
    /// The relief of <paramref name="picture"/> (turned upright and scaled down to <see cref="MaxGrid"/> points a side).
    /// With <paramref name="texture"/> the picture is the material's texture; otherwise each vertex has its pixel's
    /// colour. Not both: glTF multiplies them, which would darken the picture.
    /// </summary>
    internal static Model3D Build(IMagickImage<byte> picture, bool texture, CancellationToken cancellationToken)
    {
        using var image = picture.Clone();
        image.AutoOrient();
        if (image.ColorSpace is not (ColorSpace.sRGB or ColorSpace.RGB))
            image.ColorSpace = ColorSpace.sRGB;
        if (image.Width > MaxGrid || image.Height > MaxGrid)
            image.Resize(new MagickGeometry(MaxGrid, MaxGrid)); // keeps the aspect ratio
        var w = (int)image.Width;
        var h = (int)image.Height;
        if (w < 2 || h < 2)
            throw new InvalidDataException("The picture is too small for a relief: it needs at least 2 × 2 pixels.");

        var rgba = image.GetPixels().ToByteArray(PixelMapping.RGBA)
            ?? throw new InvalidDataException("The picture's pixels could not be read.");
        return Build(rgba, w, h, texture, cancellationToken);
    }

    /// <summary>The relief of <paramref name="w"/> × <paramref name="h"/> RGBA pixels (8 bits each, rows top down).</summary>
    internal static Model3D Build(byte[] rgba, int w, int h, bool texture, CancellationToken cancellationToken)
    {
        // The longer side is LongestSideMm from the first pixel's centre to the last one's, the other in proportion.
        var step = LongestSideMm / (Math.Max(w, h) - 1);
        var width = step * (w - 1);
        var depth = step * (h - 1);

        // Per pixel: the height of the top, and the colour with transparency blended into the background.
        var heights = new float[w * h];
        var colors = new Vector4[w * h];
        for (var p = 0; p < w * h; p++)
        {
            var rgb = new Vector3(rgba[p * 4], rgba[p * 4 + 1], rgba[p * 4 + 2]) / 255f;
            var alpha = rgba[p * 4 + 3] / 255f;
            heights[p] = BaseMm + ReliefMm * Vector3.Dot(rgb, new Vector3(0.299f, 0.587f, 0.114f)) * alpha;
            colors[p] = new Vector4(rgb * alpha + Background * (1 - alpha), 1);
        }

        var part = new MeshPart { Name = "relief", Normals = [], TexCoords = texture ? [] : null, Colors = texture ? null : [] };
        var model = new Model3D();
        model.Parts.Add(part);

        // Picture rows run along +Z (away from the top edge) and columns along +X, so the picture reads upright when
        // seen from above; the base is at Y = 0 and the model centred on X and Z.
        Vector3 At(int pixel, float y) => new((pixel % w) * step - width / 2, y, (pixel / w) * step - depth / 2);
        Vector2 Uv(int pixel) => new((pixel % w) / (float)(w - 1), 1 - (pixel / w) / (float)(h - 1));
        int Vertex(int pixel, float y) =>
            part.AddVertex(At(pixel, y), Vector3.Zero, Uv(pixel), colors[pixel]);

        // Top and base: a grid of vertices each, two triangles per square (the base's facing down).
        foreach (var top in new[] { true, false })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = part.VertexCount;
            for (var p = 0; p < w * h; p++)
                Vertex(p, top ? heights[p] : 0);
            for (var j = 0; j < h - 1; j++)
            {
                for (var i = 0; i < w - 1; i++)
                {
                    var a = first + j * w + i;
                    var b = a + 1;
                    var c = a + w;
                    var d = c + 1;
                    if (top)
                    {
                        part.AddTriangle(a, c, b);
                        part.AddTriangle(b, c, d);
                    }
                    else
                    {
                        part.AddTriangle(a, b, c);
                        part.AddTriangle(b, d, c);
                    }
                }
            }
        }

        // Walls: around the edge counter-clockwise seen from above (back left to right, right to the front, front to
        // the left, left to the back), each side with its own vertices so the corners stay sharp. A wall has its edge
        // pixels' colour from top to bottom.
        int[][] sides =
        [
            [.. Enumerable.Range(0, w).Select(i => (h - 1) * w + i)],
            [.. Enumerable.Range(0, h).Select(j => (h - 1 - j) * w + w - 1)],
            [.. Enumerable.Range(0, w).Select(i => w - 1 - i)],
            [.. Enumerable.Range(0, h).Select(j => j * w)],
        ];
        foreach (var side in sides)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = part.VertexCount;
            foreach (var pixel in side)
            {
                Vertex(pixel, heights[pixel]);
                Vertex(pixel, 0);
            }
            for (var k = 0; k < side.Length - 1; k++)
            {
                var top0 = first + k * 2;
                var bottom0 = top0 + 1;
                var top1 = top0 + 2;
                var bottom1 = top0 + 3;
                part.AddTriangle(bottom0, bottom1, top1);
                part.AddTriangle(bottom0, top1, top0);
            }
        }

        part.ComputeNormals();

        if (texture)
        {
            var rgb = new byte[w * h * 3];
            for (var p = 0; p < w * h; p++)
            {
                rgb[p * 3] = MeshMath.ToByte(colors[p].X);
                rgb[p * 3 + 1] = MeshMath.ToByte(colors[p].Y);
                rgb[p * 3 + 2] = MeshMath.ToByte(colors[p].Z);
            }
            using var picture = new MagickImage();
            picture.ReadPixels(rgb, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGB));
            model.Materials.Add(new Material3D
            {
                Name = "relief",
                BaseColorTexture = new Texture3D(picture.ToByteArray(MagickFormat.Png), "png", "relief"),
            });
            part.Material = 0;
        }

        model.Validate();
        return model;
    }
}
