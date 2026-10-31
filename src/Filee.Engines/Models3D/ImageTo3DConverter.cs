// Converts 2D raster images into 3D models (GLB, glTF, STL, OBJ, 3MF, PLY) as watertight relief / lithophane solid meshes.

using System.Numerics;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Models3D;

/// <summary>
/// Converts 2D images into real, solid 3D relief models with front relief, side walls, and flat base.
/// </summary>
public sealed class ImageTo3DConverter : IConverter
{
    public static readonly string[] ImageFormats =
    [
        "png", "jpg", "jpeg", "webp", "bmp", "tiff", "gif", "tga", "avif", "ico", "heic"
    ];

    public static readonly string[] ModelFormats = ModelConverter.Formats;

    public string Id => "image3d";
    public string DisplayName => "Image to 3D";
    public int MaxParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. from source in ImageFormats
           from target in ModelFormats
           select new ConversionEdge(source, target, Cost: 25, DirectOnly: true)
    ];

    public EngineStatus GetStatus() =>
        EngineStatus.Available("2D Image → 3D Model (Relief / Lithophane)", EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            progress?.Report(0.1);
            using var image = new MagickImage(step.InputPath);
            image.AutoOrient();

            // Downsample large images to a clean grid to keep conversion snappy and memory lean
            const int maxGrid = 200;
            var width = image.Width;
            var height = image.Height;
            if (width > maxGrid || height > maxGrid)
            {
                var scale = Math.Min((double)maxGrid / width, (double)maxGrid / height);
                var newW = Math.Max(2, (int)Math.Round(width * scale));
                var newH = Math.Max(2, (int)Math.Round(height * scale));
                image.Resize(new MagickGeometry((uint)newW, (uint)newH) { IgnoreAspectRatio = false });
            }

            var w = (int)image.Width;
            var h = (int)image.Height;
            if (w < 2 || h < 2)
                throw new InvalidDataException("Image is too small to build a 3D mesh.");

            progress?.Report(0.3);
            var rgba = image.GetPixels().ToByteArray(PixelMapping.RGBA)
                ?? throw new InvalidDataException("Failed to decode image pixels.");

            var model = BuildReliefModel(image, rgba, w, h, cancellationToken);
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
                    try { File.Delete(output); } catch { /* best effort cleanup */ }
                }
                throw;
            }

            progress?.Report(1.0);
            return (IReadOnlyList<string>)outputs;
        }, cancellationToken);

    /// <summary>
    /// Constructs a watertight 3D solid model with top relief, flat base, and 4 perimeter walls.
    /// </summary>
    internal static Model3D BuildReliefModel(MagickImage image, byte[] rgba, int w, int h, CancellationToken ct)
    {
        var model = new Model3D();
        var part = new MeshPart
        {
            Name = "ReliefMesh",
            Normals = [],
            Colors = [],
            TexCoords = []
        };
        model.Parts.Add(part);

        // Dimensions in millimetres (10 cm max dimension, standard 3D printing & viewing scale)
        const float maxDimMm = 100.0f;
        const float baseThicknessMm = 2.0f;
        const float reliefHeightMm = 8.0f;

        float widthMm;
        float heightMm;
        if (w >= h)
        {
            widthMm = maxDimMm;
            heightMm = maxDimMm * ((float)(h - 1) / (w - 1));
        }
        else
        {
            heightMm = maxDimMm;
            widthMm = maxDimMm * ((float)(w - 1) / (h - 1));
        }

        var wallBaseColor = new Vector4(0.85f, 0.85f, 0.85f, 1.0f);

        // 1. Top relief surface vertices
        for (var j = 0; j < h; j++)
        {
            ct.ThrowIfCancellationRequested();
            var z = (j / (float)(h - 1) - 0.5f) * heightMm;
            var vCoord = 1.0f - j / (float)(h - 1); // UV origin bottom-left

            for (var i = 0; i < w; i++)
            {
                var x = (i / (float)(w - 1) - 0.5f) * widthMm;
                var uCoord = i / (float)(w - 1);

                var idx = (j * w + i) * 4;
                var r = rgba[idx] / 255.0f;
                var g = rgba[idx + 1] / 255.0f;
                var b = rgba[idx + 2] / 255.0f;
                var a = rgba[idx + 3] / 255.0f;

                // Luminance calculation
                var lum = (0.299f * r + 0.587f * g + 0.114f * b) * a;
                var y = baseThicknessMm + reliefHeightMm * lum;

                part.AddVertex(
                    new Vector3(x, y, z),
                    normal: Vector3.UnitY,
                    uv: new Vector2(uCoord, vCoord),
                    color: new Vector4(r, g, b, 1.0f));
            }
        }

        // 2. Top relief surface triangles (Normal pointing UP +Y)
        for (var j = 0; j < h - 1; j++)
        {
            for (var i = 0; i < w - 1; i++)
            {
                var top00 = j * w + i;
                var top10 = j * w + (i + 1);
                var top01 = (j + 1) * w + i;
                var top11 = (j + 1) * w + (i + 1);

                part.AddTriangle(top00, top01, top10);
                part.AddTriangle(top10, top01, top11);
            }
        }

        // 3. Bottom base surface vertices (Flat base at Y = 0)
        var bottomBaseIndex = part.VertexCount;
        for (var j = 0; j < h; j++)
        {
            ct.ThrowIfCancellationRequested();
            var z = (j / (float)(h - 1) - 0.5f) * heightMm;
            var vCoord = 1.0f - j / (float)(h - 1);

            for (var i = 0; i < w; i++)
            {
                var x = (i / (float)(w - 1) - 0.5f) * widthMm;
                var uCoord = i / (float)(w - 1);

                part.AddVertex(
                    new Vector3(x, 0.0f, z),
                    normal: -Vector3.UnitY,
                    uv: new Vector2(uCoord, vCoord),
                    color: wallBaseColor);
            }
        }

        // 4. Bottom base triangles (Normal pointing DOWN -Y, reverse winding)
        for (var j = 0; j < h - 1; j++)
        {
            for (var i = 0; i < w - 1; i++)
            {
                var bot00 = bottomBaseIndex + j * w + i;
                var bot10 = bottomBaseIndex + j * w + (i + 1);
                var bot01 = bottomBaseIndex + (j + 1) * w + i;
                var bot11 = bottomBaseIndex + (j + 1) * w + (i + 1);

                part.AddTriangle(bot00, bot10, bot01);
                part.AddTriangle(bot10, bot11, bot01);
            }
        }

        // 5. Perimeter side walls (Connecting top perimeter to bottom perimeter with split vertices for sharp corners)
        // Front wall (j = 0, Z = -heightMm / 2) -> faces -Z
        var frontTopStart = part.VertexCount;
        for (var i = 0; i < w; i++)
        {
            var pTop = part.Position(i);
            part.AddVertex(pTop, -Vector3.UnitZ, new Vector2(i / (float)(w - 1), 1.0f), wallBaseColor);
            part.AddVertex(new Vector3(pTop.X, 0.0f, pTop.Z), -Vector3.UnitZ, new Vector2(i / (float)(w - 1), 0.0f), wallBaseColor);
        }
        for (var i = 0; i < w - 1; i++)
        {
            var t0 = frontTopStart + i * 2;
            var b0 = t0 + 1;
            var t1 = frontTopStart + (i + 1) * 2;
            var b1 = t1 + 1;

            part.AddTriangle(b0, t0, b1);
            part.AddTriangle(b1, t0, t1);
        }

        // Back wall (j = h - 1, Z = +heightMm / 2) -> faces +Z
        var backTopStart = part.VertexCount;
        var lastRowOffset = (h - 1) * w;
        for (var i = 0; i < w; i++)
        {
            var pTop = part.Position(lastRowOffset + i);
            part.AddVertex(pTop, Vector3.UnitZ, new Vector2(i / (float)(w - 1), 1.0f), wallBaseColor);
            part.AddVertex(new Vector3(pTop.X, 0.0f, pTop.Z), Vector3.UnitZ, new Vector2(i / (float)(w - 1), 0.0f), wallBaseColor);
        }
        for (var i = 0; i < w - 1; i++)
        {
            var t0 = backTopStart + i * 2;
            var b0 = t0 + 1;
            var t1 = backTopStart + (i + 1) * 2;
            var b1 = t1 + 1;

            part.AddTriangle(b0, b1, t0);
            part.AddTriangle(b1, t1, t0);
        }

        // Left wall (i = 0, X = -widthMm / 2) -> faces -X
        var leftTopStart = part.VertexCount;
        for (var j = 0; j < h; j++)
        {
            var pTop = part.Position(j * w);
            part.AddVertex(pTop, -Vector3.UnitX, new Vector2(j / (float)(h - 1), 1.0f), wallBaseColor);
            part.AddVertex(new Vector3(pTop.X, 0.0f, pTop.Z), -Vector3.UnitX, new Vector2(j / (float)(h - 1), 0.0f), wallBaseColor);
        }
        for (var j = 0; j < h - 1; j++)
        {
            var t0 = leftTopStart + j * 2;
            var b0 = t0 + 1;
            var t1 = leftTopStart + (j + 1) * 2;
            var b1 = t1 + 1;

            part.AddTriangle(b0, b1, t0);
            part.AddTriangle(b1, t1, t0);
        }

        // Right wall (i = w - 1, X = +widthMm / 2) -> faces +X
        var rightTopStart = part.VertexCount;
        for (var j = 0; j < h; j++)
        {
            var pTop = part.Position(j * w + (w - 1));
            part.AddVertex(pTop, Vector3.UnitX, new Vector2(j / (float)(h - 1), 1.0f), wallBaseColor);
            part.AddVertex(new Vector3(pTop.X, 0.0f, pTop.Z), Vector3.UnitX, new Vector2(j / (float)(h - 1), 0.0f), wallBaseColor);
        }
        for (var j = 0; j < h - 1; j++)
        {
            var t0 = rightTopStart + j * 2;
            var b0 = t0 + 1;
            var t1 = rightTopStart + (j + 1) * 2;
            var b1 = t1 + 1;

            part.AddTriangle(b0, t0, b1);
            part.AddTriangle(b1, t0, t1);
        }

        // Compute smooth normals across top relief
        part.ComputeNormals();

        // Attach texture material for GLB / glTF / OBJ
        try
        {
            var textureBytes = image.ToByteArray(MagickFormat.Png);
            if (textureBytes is not null && textureBytes.Length > 0)
            {
                var texture = new Texture3D(textureBytes, "png", "texture");
                var material = new Material3D
                {
                    Name = "ReliefMaterial",
                    BaseColor = Vector4.One,
                    BaseColorTexture = texture
                };
                model.Materials.Add(material);
                part.Material = 0;
            }
        }
        catch
        {
            // Fallback to vertex colors if texture encoding fails
        }

        model.Validate();
        return model;
    }
}
