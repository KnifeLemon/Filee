// Source engine models (.mdl with .vvd and .dx90.vtx, #1526): read in their bind pose into Filee's 3D model (Y up,
// millimetres, counter-clockwise), with their textures found through the .vmt files in the "materials" folder above them
// and decoded from VTF. The test models are built by SourceModelBuilder.

using System.Buffers.Binary;
using System.Numerics;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using Filee.Engines.Models3D;
using ImageMagick;

namespace Filee.Engines.Tests;

public class SourceModelTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<IReadOnlyList<string>> ConvertOkAsync(string source, string target)
    {
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return [.. job.Outputs];
    }

    /// <summary>
    /// An addon folder as Garry's Mod mounts it: models/props/tile.mdl (two materials, "brick" and "glass"), the brick's
    /// material (in a differently cased folder) pointing at a red 16 × 16 VTF, and no material for the glass.
    /// </summary>
    private string Addon(bool fixups = false)
    {
        var root = fx.NewFolder();
        var mdl = SourceModelBuilder.Write(Path.Combine(root, "models", "props"), "tile", "models\\props\\", ["brick", "glass"],
            SourceModelBuilder.TileAndTriangle(), fixups: fixups);
        var materials = Directory.CreateDirectory(Path.Combine(root, "materials", "Models", "Props")).FullName;
        File.WriteAllText(Path.Combine(materials, "brick.vmt"), """
            // a comment
            "VertexLitGeneric"
            {
                "$basetexture" "models\props\brick_color"
                "$nocull" "1"
            }
            """);
        using var red = new MagickImage(MagickColors.Red, 16, 16);
        VtfWriter.Write([red], Path.Combine(materials, "brick_color.vtf"));
        return mdl;
    }

    [Fact]
    public void Mdl_is_a_3d_model_that_converts_in_one_step()
    {
        Assert.Equal("mdl", FormatRegistry.Detect("Tile.MDL")?.Id);
        var planner = fx.Catalog.CreatePlanner(["model3d"]);
        foreach (var target in new[] { "obj", "glb", "stl", "3mf", "ply", "gltf" })
            Assert.Single(planner.Plan("mdl", target)!.Steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_model_is_read_in_filees_axes_units_and_winding(bool fixups)
    {
        var model = ModelConverter.Read(Addon(fixups), "mdl", null, Ct);

        Assert.Equal(2, model.Parts.Count);
        Assert.Equal(3, model.TriangleCount);
        Assert.Equal(7, model.VertexCount);
        // 10 inches is 254 mm; Source's Z up becomes Y up, its Y becomes −Z.
        var (min, max) = model.Bounds();
        Assert.Equal(new Vector3(0, 0, -254), min, new Vector3Comparer());
        Assert.Equal(new Vector3(254, 127, 0), max, new Vector3Comparer());
        foreach (var part in model.Parts)
        {
            // Counter-clockwise: every face points the way its vertex normals (up) do.
            for (var t = 0; t < part.TriangleCount; t++)
                Assert.True(Vector3.Dot(part.FaceNormal(t), Vector3.UnitY) > 0.99f);
            Assert.All(Enumerable.Range(0, part.VertexCount), v => Assert.Equal(Vector3.UnitY, part.Normal(v), new Vector3Comparer()));
        }
        // UVs from the bottom left: the tile's corner at the origin had v = 1 from the top.
        var tile = model.Parts[0];
        var origin = Enumerable.Range(0, tile.VertexCount).Single(v => tile.Position(v) == Vector3.Zero);
        Assert.Equal(Vector2.Zero, tile.TexCoord(origin));
    }

    [Fact]
    public void Its_textures_are_found_through_the_materials()
    {
        var model = ModelConverter.Read(Addon(), "mdl", null, Ct);

        var brick = model.Materials.Single(m => m.Name == "brick");
        Assert.True(brick.DoubleSided);
        var texture = Assert.IsType<Texture3D>(brick.BaseColorTexture);
        Assert.Equal("png", texture.Extension);
        using var image = new MagickImage(texture.Data);
        Assert.Equal((16u, 16u), (image.Width, image.Height));
        using var pixels = image.GetPixels();
        var pixel = pixels.GetPixel(8, 8)!.ToColor()!;
        Assert.InRange(pixel.R, 240, 255);
        Assert.InRange(pixel.G, 0, 15);

        // The game's own texture (no material in the addon) leaves the material plain.
        Assert.Null(model.Materials.Single(m => m.Name == "glass").BaseColorTexture);
    }

    [Fact]
    public async Task A_model_becomes_an_obj_with_its_texture_and_a_glb()
    {
        var mdl = Addon();

        var obj = await ConvertOkAsync(mdl, "obj");
        Assert.Contains(obj, p => p.EndsWith(".obj", StringComparison.Ordinal));
        Assert.Contains(obj, p => p.EndsWith(".mtl", StringComparison.Ordinal));
        Assert.Contains(obj, p => p.EndsWith(".png", StringComparison.Ordinal));

        var glb = Assert.Single(await ConvertOkAsync(mdl, "glb"));
        var back = ModelConverter.Read(glb, "glb", null, Ct);
        Assert.Equal(3, back.TriangleCount);
        Assert.Contains(back.Materials, m => m.BaseColorTexture is not null);
    }

    [Fact]
    public void A_model_without_its_vvd_or_with_another_builds_vvd_is_refused_clearly()
    {
        var folder = fx.NewFolder();
        var mdl = SourceModelBuilder.Write(folder, "box", "", ["brick", "glass"], SourceModelBuilder.TileAndTriangle());
        File.Delete(Path.ChangeExtension(mdl, ".vvd"));
        var missing = Assert.Throws<FileNotFoundException>(() => ModelConverter.Read(mdl, "mdl", null, Ct));
        Assert.Contains("box.vvd", missing.Message);

        mdl = SourceModelBuilder.Write(folder, "box2", "", ["brick", "glass"], SourceModelBuilder.TileAndTriangle(), vvdChecksum: 42);
        var other = Assert.Throws<InvalidDataException>(() => ModelConverter.Read(mdl, "mdl", null, Ct));
        Assert.Contains("checksum", other.Message);

        var notModel = Path.Combine(folder, "quake.mdl");
        File.WriteAllBytes(notModel, "IDPO"u8.ToArray());
        File.WriteAllBytes(Path.Combine(folder, "quake.vvd"), []);
        File.WriteAllBytes(Path.Combine(folder, "quake.dx90.vtx"), []);
        Assert.Throws<InvalidDataException>(() => ModelConverter.Read(notModel, "mdl", null, Ct));
    }

    [Fact]
    public void Triangles_are_counted_against_one_budget_even_where_meshes_share_a_table()
    {
        var folder = fx.NewFolder();
        var mdl = SourceModelBuilder.Write(folder, "shared", "", ["brick", "glass"], SourceModelBuilder.TileAndTriangle());
        // The first mesh (at 64 in the .vtx) now claims two strip groups: its own and the second mesh's, which follows it.
        // The second mesh's triangle is read twice, so the model has 4 triangles though its file holds 3.
        var vtx = Path.ChangeExtension(mdl, ".dx90.vtx");
        var bytes = File.ReadAllBytes(vtx);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(64), 2);
        File.WriteAllBytes(vtx, bytes);

        Assert.Equal(4, SourceModel.Load(mdl, Ct, maxTriangles: 4).Meshes.Sum(m => m.Triangles.Count / 3));
        var refused = Assert.Throws<ConversionRefusedException>(() => SourceModel.Load(mdl, Ct, maxTriangles: 3));
        Assert.Equal("error.document_too_large", refused.ErrorKey);
    }

    [Fact]
    public void A_material_file_larger_than_the_cap_leaves_the_material_plain()
    {
        var folder = fx.NewFolder();
        var vmt = Path.Combine(folder, "huge.vmt");
        File.WriteAllText(vmt, "\"VertexLitGeneric\" { $basetexture \"a/b\" }" + new string(' ', (int)Vmt.MaxBytes));
        Assert.Null(Vmt.Read(vmt, _ => null).BaseTexture);

        File.WriteAllText(vmt, "\"VertexLitGeneric\" { $basetexture \"a/b\" }");
        Assert.Equal("a/b", Vmt.Read(vmt, _ => null).BaseTexture);
    }

    [Fact]
    public void Material_files_are_read_as_keyvalues()
    {
        var tokens = Vmt.Tokens("""
            "UnlitGeneric" // shader
            {
                $basetexture "a/b c"
                "$alphatest" 1
            }
            """);
        Assert.Equal(["UnlitGeneric", "{", "$basetexture", "a/b c", "$alphatest", "1", "}"], tokens);
    }

    private sealed class Vector3Comparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.01f;
        public int GetHashCode(Vector3 v) => 0;
    }
}
